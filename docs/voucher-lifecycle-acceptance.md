# Voucher lifecycle acceptance — 5.10.8.E

- Reviewed commit: `9f00e7493155a90682785e3377bfe3aa74c45e14` (develop, 2026-11-24).
- Status: **PENDING runtime evidence and review; rollout remains OFF.**
- Scope: COD, one optional voucher, no reservation at preview.

## Static findings and corrective action

| Component | Observation |
|---|---|
| CheckoutPricingService | Canonical code, lookup by ID after shared voucher lock, live count and time, pure pricing policy. |
| CheckoutService | Cart lock, replay before cart validation, counter increment + stock decrement + Order/Payment/cart writes in one transaction. |
| OrderCancellationService | Order lock then voucher lock; authorization, transition, payment and item validation; release only non-cancelled use, preserve financial history. |
| VoucherService | Admin update and activation use same voucher lock; commercial terms frozen after history; no counter overwrite by terms update. |
| VoucherUsageWriteRepository | Transaction prerequisite and expected-counter predicates; decrement permits inactive/expired vouchers. Caller must hold application lock. |
| UnitOfWork / DI | ReadCommitted, exclusive transaction-owned applocks, shared scoped DbContext; SaveChanges + Commit before return. Failed request scope must be discarded. |
| AdminOrderStatusService / CodPaymentSettlementService | Order lock; direct transition to Cancelled rejected; settlement doesn't change voucher usage. |
| Idempotency | V1 preserved, V2 normalized code; replay performs no new redemption. **Finding:** separate order/payment reads could straddle cancellation commit. Supplied fix acquires Order lock after cart lock, re-reads no-tracking Order. Regression test forces cancellation between lookup and acquisition. |
| Migration | `20261124130728_AddVoucherUsageCompositeIndex` adds nonunique composite index (VoucherId, UserId, Status); existing index retained. |

## Invariants and boundaries

- UsedCount equals committed non-cancelled linked orders, including Delivered; never negative; must respect global and per-user limits.
- No extra consumption on replay or preview. A cancelled order retains VoucherId, amount, fingerprint and items; a new checkout requires a new request ID.
- Lock ordering: new checkout Cart -> Voucher -> Product; cancellation Order -> Voucher -> Product; replay Cart -> Order; admin voucher mutation Voucher; admin status and settlement Order. Product updates ordered by ProductId. No reviewed order-locked path acquires a cart lock.
- Preview/customer availability are advisory, separate reads may observe concurrent changes. Revalidate under the transaction at checkout.
- This review is not a proof against SQL deadlocks, arbitrary SQL writes, or future mutation paths. No runtime or query plan was executed here.

## Acceptance evidence (fill locally; all pending)

| Gate | Required evidence | Status |
|---|---|---|
| A | Build with warnings as errors | PENDING |
| B | 8 VoucherCancellationConcurrencyTests cases, including new replay interleaving | PENDING |
| C | Five additional runs of cancellation/checkout/lock integration groups; all pass | PENDING |
| D | Full solution test TRX; all passed, no skipped | PENDING |
| E | `voucher-usage-index-verify.sql` schema PASS and no anomaly rows | PENDING |
| F | `voucher-lifecycle-acceptance.sql` all 10 checks zero violations | PENDING |
| G | Migration history and no pending model changes; inspect current target DB | PENDING |
| H | Application DI still registers `CheckoutVoucherRollout(false)` | Source reviewed; verify locally |

Record commit, test database name (no credentials), date, test counts, TRX paths, SQL output, and reviewer decision. Do not mark acceptance based on source review alone. Test database must be idle during audits. Do not auto-repair historical anomalies; review them with the business owner. The old incomplete V2 fingerprint cannot generally be reconstructed from its hash; review request logs if affected.

## Next gate

5.10.9: HTTP tests with a test-only enabled rollout, real JWT roles and Customer/Admin routes, preview/checkout/cancel/replay and conflict response contract. Only after all gates pass decide on enabling normal checkout. A rollback of the rollout flag blocks new redemption but must preserve restoration for historical orders. Do not revert cancellation support on a database that contains redeemed orders.
