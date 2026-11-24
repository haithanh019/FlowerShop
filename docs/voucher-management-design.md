# FlowerShop — Voucher Management Design & Baseline Audit (5.10.1)

- Date: 2026-11-24
- Branch: develop
- Inspected baseline: `eeeff2fad36ef0420ed6d2d6f431f0417e85d7e7`
- Scope: single-shop COD MVP, one optional order-level voucher per checkout.
- Decision: **DESIGN PROPOSED; SQL BASELINE PENDING**. No schema or runtime change in this step.

## Source observations

- `Voucher` and `DiscountType` already exist; `Percentage = 1`, `FixedAmount = 2`.
- `VoucherConfiguration` and `InitialCreate` declare 9 check constraints, unique `UX_Vouchers_Code`, and `IX_Vouchers_IsActive_StartAt_EndAt`.
- `Orders.VoucherId` is nullable, FK to Vouchers with NoAction; `IX_Orders_VoucherId` exists.
- Order monetary columns use decimal(18,2); constraints require discount <= subtotal and total = subtotal + shipping - discount.
- `CheckoutRequestDto` has no VoucherCode; Checkout currently hardcodes discount and shipping to zero.
- `CheckoutRequestFingerprint` writes version 1 and hashes request ID, shipping address ID, payment method and note; replay precedes cart/address/stock checks.
- Payment requires Amount > 0; `OrderFinancialConsistency.HasValidCodPayment` also requires positive order total.
- Cancellation uses order-scoped transaction lock and restores stock exactly once; currently no voucher restoration.
- UnitOfWork supports one resource lock per locked transaction, and rejects nested transactions. Product stock updates execute within the shared transaction.

## Proposed business rules

- Code: trim, uppercase invariant, ASCII `[A-Z0-9_-]{1,50}`. Immutable after creation; database uniqueness is final arbiter. Audit legacy normalized collisions before using normalized lookup.
- Description: optional, trim, blank to null, <=500 chars.
- Money: decimal(18,2), 0..9999999999999999.99 as applicable; reject excess scale rather than silently round admin inputs. DiscountValue positive; percentage <=100. MinOrderAmount >=0; optional cap positive. MaxDiscountAmount applies only to percentage; fixed amount requires null (application policy, existing DB allows both).
- Optional UsageLimit and PerUserLimit must be positive. UsedCount is server-owned, initially zero. Reject decreasing UsageLimit below UsedCount; when changing PerUserLimit, reject values below any existing noncancelled usage by a customer. Null means unlimited; no need to require PerUserLimit <= UsageLimit.
- Use UTC, millisecond precision. Valid interval [StartAt, EndAt); StartAt < EndAt after precision normalization. Require timezone/offset for incoming times. Admin may deactivate; do not hard-delete vouchers or change immutable Code. Proposed policy: commercial terms may be edited before first successful use; after any historical linked order exists, freeze DiscountType, DiscountValue, MinOrderAmount, MaxDiscountAmount, StartAt; allow description, activation, end date and validated limits to change. Do not derive historical price from current voucher terms.
- Only active Customer can preview or redeem. Preview derives subtotal from server-side cart/current product prices; it is a quote, not a reservation. Checkout revalidates. One voucher, no stacking, excludes shipping from minimum spend and discount base.
- Calculation: percentage = subtotal * value / 100; apply optional percentage cap; fixed amount = value; clamp to subtotal; round to 2 decimal places, MidpointRounding.AwayFromZero. Reject voucher yielding zero discount or resulting total <=0 (existing COD contract). No implicit minimum-payable alteration. Order.TotalAmount = Subtotal + ShippingFee - DiscountAmount; Payment.Amount = TotalAmount.
- Usage: count committed non-Cancelled orders linked by VoucherId (Pending through Delivered); a successfully cancelled order releases one use, retains VoucherId and financial snapshots. UsedCount equals that count, including legacy orders only after reviewed reconciliation. An expired/inactive voucher can still have usage restored. No usage changes on preview/replay/failed checkout. Guard int.MaxValue.
- Replay: preserve the original byte-for-byte v1 hash for requests with no voucher; version 2 for requests with nonblank normalized VoucherCode, including code in hash. Changed voucher or removal/addition under same request ID is a mismatch. Blank/null treated as no voucher if DTO policy permits. Replay uses saved order/payment snapshots even after voucher expiry, editing or cancellation; never recompute or increment usage.
- Concurrency: extend UoW with acquisition of additional transaction-owned resource locks without nesting transactions. All voucher mutation/redeem/release paths acquire `flower-shop:voucher:{voucherId:N}`. Lookup immutable canonical Code -> Id, then re-read under lock. Checkout retains cart lock; cancellation retains order lock; order of acquisitions: cart OR order lock -> voucher lock -> product updates sorted by ProductId. Admin update takes voucher lock only. Preview does not acquire reservation. Verify locks and shared DbContext with cross-user tests. Ensure current count and per-user count read after locking; no stale tracked entities; use indexed count query, not full order materialization.
- Add composite index `(VoucherId, UserId, Status)` in a later reviewed migration for usage counts (consider replacing redundant single-column index only after query-plan review). No migration now. No ledger required for this COD MVP, but online refunds/reservations may need one.
- Orders with existing inconsistent voucher references or counters must be investigated; do not auto-repair based on an unverified legacy assumption.

## Roadmap

5.10.2 DTOs and validation (admin create/update, response; preview contract). 5.10.3 admin query. 5.10.4 create/update/deactivate and immutable code policy; coordinate locking contract. 5.10.5 deterministic eligibility and discount calculation tests. 5.10.6 admin and customer preview APIs. 5.10.7–5.10.8 design/implement additional lock support, indexed usage queries, versioned fingerprint, checkout redemption and cancellation release as one atomic rollout; no partially enabled voucher checkout. 5.10.9 HTTP/concurrency/regression tests and release gate.

## Baseline verification

Run `scripts/voucher-baseline-audit.sql` only against FlowerShop_Test. Review declared column/index/constraint metadata, code normalization collisions and usage discrepancies. Empty tables do not prove functionality. Runtime evidence: local build, full test suite, migration status; keep PENDING until verified. Never commit secrets or real customer data.
