# FlowerShop — Order Management Release Gate

- Date: 2026-11-23
- Branch: `develop`
- Baseline commit: `e7178b94a6ee3dbf6ca9d1692ee087ab59160957`
- Scope: backend COD MVP; not the whole FlowerShop application
- Decision: **PENDING** until local evidence is recorded

## 1. Scope and implementation

- [x] COD checkout with request fingerprint and idempotency
- [x] Checkout transaction, conditional stock deduction, and rollback logic
- [x] Customer Order History / Detail
- [x] Customer/Admin cancellation with stock restoration
- [x] Admin Order Status lifecycle policy
- [x] COD payment collection settlement and delivery guard
- [x] Admin Order List / Detail with filters and pagination
- [x] JWT role and active-account checks in current implementation
- [x] Repository, application, and HTTP integration test source files exist

The checkboxes above describe inspected source code and test coverage presence. They do **not** claim that a local test run has passed.

## 2. Release evidence — fill in after local execution

- [ ] Clean `develop` working tree at baseline (or reviewed staged changes)
- [ ] `dotnet build FlowerShop.slnx --no-restore -warnaserror` passed
- [ ] `dotnet test FlowerShop.slnx --no-restore` passed
- [ ] `AdminOrderQueryHttpTests` passed
- [ ] `CrossUserCheckoutConcurrencyHttpTests` passed 5 consecutive times
- [ ] `CodPaymentConcurrencyHttpTests` passed 5 consecutive times
- [ ] `OrderManagementHttpTests` passed
- [ ] `CheckoutTransactionTests` passed
- [ ] Applied migrations include `InitialCreate` and `AddCheckoutRequestFingerprint`
- [ ] `UX_Orders_OrderCode` index exists, is unique, and is enabled
- [ ] SQL audit scoped to new COD Orders returned ZERO anomaly rows
- [ ] Duplicate `OrderCode` query returned ZERO rows
- [ ] Any test/database anomalies investigated, corrected, and retested

### Recorded results

| Check | Result | Evidence (command/log/date) |
|---|---|---|
| Build with warnings as errors | PENDING | |
| Full test suite | PENDING | |
| Cross-user Checkout concurrency ×5 | PENDING | |
| COD settlement concurrency ×5 | PENDING | |
| Migrations | PENDING | |
| SQL index | PENDING | |
| Financial / lifecycle audit | PENDING | |
| Duplicate OrderCode | PENDING | |

### SQL audit scope

Execute `scripts/order-management-integrity-audit.sql` against `FlowerShop_Test` only. It audits persisted Orders created through the new checkout mechanism, identified by a non-null `CheckoutRequestFingerprint`.

**If the audited count is zero, report the data audit as INCONCLUSIVE**, not Passed. The integration suite may clean up its own seeded Orders. In that case, create a normal test Order through the API in the test database and rerun the audit, or provide alternative persisted test evidence.

Legacy Orders (null fingerprint) and intentionally inconsistent integration-test fixtures are excluded. A clean audit is not proof that every legacy record is valid.

## 3. Outstanding work outside COD Order Management

- Registration, profile, and address management APIs (required for end-to-end Customer onboarding)
- Next.js/JavaScript storefront and Admin Dashboard
- Frontend/backend end-to-end testing and deployment
- Voucher, Review, Notification and online payment gateway, depending on MVP scope

## 4. Release decision

- [ ] **PASS** — all mandatory release evidence above is green; close backend COD Order Management module.
- [ ] **BLOCKED** — a reproducible test, migration, integrity, or security failure remains; fix and rerun.

Decision owner: ____________________
Verification date: __________________
Final commit: ______________________
Notes: _____________________________
