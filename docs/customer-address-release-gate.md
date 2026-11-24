# FlowerShop — Customer Address Management Release Gate (5.8.8)

- Review date: 2026-11-23
- Branch: `develop`
- Source baseline inspected: `f70d47b58d0c6b2293873cc721b2ccac367d492f`
- Scope: customer-owned address CRUD, default selection, JWT authorization, concurrency, historical Order snapshot immutability
- Decision: **PENDING** — execute tests and review SQL evidence on the local `FlowerShop_Test` database before declaring PASS

## 1. Source-code findings

- [x] `UserAddressesController` has six routes under `/api/me/addresses`.
- [x] Controller requires `[Authorize(Roles = "Customer")]` and uses JWT `sub` as UserId.
- [x] `UserAddressService` enforces `(AddressId, UserId)` ownership for addressed operations.
- [x] Create, Update, Delete and SetDefault share a user-scoped SQL application lock.
- [x] Default replacement writes are sequenced within a transaction to respect the filtered unique index.
- [x] Checkout stores shipping information as fields on `Order`, not a foreign key to `UserAddress`.
- [x] `UserAddressDtoValidationTests` and service-layer integration tests exist.
- [x] `UserAddressHttpTests.cs` has been prepared for this gate (not yet certified by runtime tests).

These checkboxes describe inspected source or test artifact presence only; they are **not** runtime test results.

## 2. HTTP acceptance coverage

| Scenario | Expected |
|---|---|
| GET list/detail | 200; only current Customer data |
| POST | 201 + Location + normalized data; first address is default |
| PUT | 200; only editable fields change; ownership/default preserved |
| DELETE | 204; fallback default chosen deterministically |
| PATCH default | 200; exactly one default; reselect is idempotent |
| Anonymous / Admin JWT | 401 / 403 |
| Cross-customer IDOR | 404 for GET/PUT/DELETE/PATCH |
| Validation / invalid GUID | 400 for invalid DTO / Guid.Empty |
| Disabled Customer with existing token | 401 |
| Concurrent PATCH | exactly one persisted default |
| Historical Order shipping snapshot | unchanged after address update and deletion |

Historical snapshot test uses a seeded `Order` row; existing Checkout HTTP tests separately verify real checkout snapshot creation.

## 3. Local evidence (must be filled after execution)

| Gate | Result | Date / sanitized command output |
|---|---|---|
| Build solution, warnings as errors | PENDING | |
| UserAddress HTTP tests | PENDING | |
| All UserAddress service/DTO tests | PENDING | |
| Concurrent HTTP SetDefault, 5 consecutive runs | PENDING | |
| Full solution test suite | PENDING | |
| Current migrations applied to FlowerShop_Test | PENDING | |
| UX_UserAddresses_OneDefault exists, unique, enabled and filtered | PENDING | |
| At most one default per Customer (SQL audit) | PENDING | |
| Customers with addresses but no default reviewed | PENDING | |
| No unexplained errors; seed cleanup verified | PENDING | |

## 4. Commands (PowerShell from solution root)

Ensure `FLOWERSHOP_TEST_CONNECTION_STRING` targets **FlowerShop_Test** with `MultipleActiveResultSets=False`.

```powershell
$project = 'FlowerShop.IntegrationTests/FlowerShop.IntegrationTests.csproj'

dotnet build FlowerShop.slnx --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'ADDRESS_BUILD_FAILED' }

dotnet test $project --no-build --filter 'FullyQualifiedName~UserAddressHttpTests'
if ($LASTEXITCODE -ne 0) { throw 'ADDRESS_HTTP_FAILED' }

dotnet test $project --no-build --filter 'FullyQualifiedName~UserAddress'
if ($LASTEXITCODE -ne 0) { throw 'ADDRESS_REGRESSION_FAILED' }

for ($i = 1; $i -le 5; $i++) {
    dotnet test $project --no-build --filter 'FullyQualifiedName~ConcurrentHttpSetDefault_ShouldKeepExactlyOneDefault'
    if ($LASTEXITCODE -ne 0) { throw "ADDRESS_HTTP_CONCURRENCY_FAILED_$i" }
}

dotnet test FlowerShop.slnx --no-build
if ($LASTEXITCODE -ne 0) { throw 'FULL_SUITE_FAILED' }
```

Run `scripts/customer-address-integrity-audit.sql` in SSMS while connected to `FlowerShop_Test`; verify the index and review all anomaly rows. The script is read-only.

The suite cleans up its own rows. **Zero residual test addresses is normal**; use test results and a fresh test transaction for direct SQL verification if necessary.

## 5. Decision

- [ ] **PASS (Customer Address Management only):** mandatory tests, concurrency runs, schema and data audit all pass; no unexplained anomaly remains.
- [ ] **BLOCKED:** a reproducible build, HTTP status, authorization, concurrency or integrity failure remains.
- [ ] **INCONCLUSIVE:** required runtime or SQL evidence was not available.

Verified by: ______________________
Date/time: ________________________
Final tested commit: ______________
Sanitized evidence reference: ______

## 6. Non-goals / next work

- User Profile Management (`/api/me/profile`)
- Frontend Next.js customer address UI
- Production secret management, CORS and penetration testing
- Full production authorization/security certification

Never commit test DB connection strings, JWT signing keys, credentials or sensitive customer records.
