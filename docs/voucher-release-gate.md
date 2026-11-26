# Voucher Management — Final Release Gate (5.10.9.C)

- Baseline: `f09f10b43888421a3f97cccdb9a62326cb27f088` (develop, 2026-11-26).
- Status: **release candidate, not approved or deployed**. No runtime test or SQL audit executed by the author of this package.
- Scope: single-shop COD MVP, one optional voucher. No new migration; require `20261124130728_AddVoucherUsageCompositeIndex`.

## Gates in order

1. While default rollout is false, copy only `scripts/test-voucher-release-gate.ps1` from this package. Stop API/writers and use isolated FlowerShop_Test with MARS disabled. Run `& .\scripts\test-voucher-release-gate.ps1 -Phase Preflight`. Confirm no skipped/failed tests. In SSMS run `scripts/voucher-usage-index-verify.sql` then `scripts/voucher-lifecycle-acceptance.sql`; expect schema PASS and all data violation counts zero. Check migration history and pending model changes. Save test TRX and SQL output outside the repo.
2. Only after preflight passes, apply all C# files in this package on a local branch. Default DI changes to true. The two HTTP suites now use normal application DI without an enabled test override. The old disabled guard uses a dedicated disabled test host; a new test seeds an order using enabled DI and verifies replay/cancellation through the disabled host.
3. Run `& .\scripts\test-voucher-release-gate.ps1 -Phase ReleaseCandidate`. Rerun both SQL audits after tests. No new migration should be generated. Keep the evidence for the exact source tested; do not deploy while either gate is failing.
4. Review any SQL anomalies manually. The audit cannot infer missing original request data from a fingerprint, prove there are no deadlocks, or certify production integrity. Record actual target environment/migrations and verify database integrity before production deployment; the provided audit scripts are intentionally restricted to FlowerShop_Test. Do not rename or point the test suite at production. Plan separate reviewed production audit, backup and release procedures.
5. Commit the release candidate only after passing both phases. Approval and deployment are separate decisions.

| Gate | Expected | Actual evidence |
|---|---|---|
| Preflight build | No warnings/errors | Pending |
| Preflight HTTP | 11 checkout + 9 cancellation | Pending |
| Preflight pricing | 7 | Pending |
| Both phases concurrency | 8, plus five regression runs | Pending |
| Release pricing | 8 (new disabled-host lifecycle test) | Pending |
| Release HTTP | 11 + 9, default DI | Pending |
| Both phases full solution | All pass, none skipped | Pending |
| Both phases SQL | Schema PASS, 10 checks zero violations | Pending |
| Model/migration | No pending changes | Pending |
| Reviewer decision | Approve or block | Pending |

## Emergency rollback

Change only `CheckoutVoucherRollout(true)` to `CheckoutVoucherRollout(false)` in Application DI; rebuild and redeploy the safe configuration after appropriate environment verification. New voucher redemption returns 409 `CHECKOUT_VOUCHER_NOT_ENABLED`. Existing v2 replay is checked before the guard; cancellation remains enabled and restores committed usage. Do not revert cancellation code or drop the composite index/migrations. The release acceptance tests intentionally expect enabled default DI, so they will fail under a disabled build. The focused disabled guard test (`CheckoutHttp_RolloutGuard_ShouldBlockNewVoucherAndPreserveV1Replay`) is suitable for disabled builds; the disabled-host lifecycle test requires an enabled host to create the fixture first. The latter demonstrates the behavior across a simulated toggle, not an actual production deployment.

## Frontend contract

See `docs/voucher-api-contract.md`. Prices are server-owned; preview is advisory. Retain the same checkoutRequestId and payload on network retry. A new order after cancellation requires a new ID. Cancellation does not repopulate the cart. A delivered order continues consuming a voucher use.
