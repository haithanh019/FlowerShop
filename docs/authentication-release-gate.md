# FlowerShop — Authentication Release Gate (COD MVP)

- Review date: 2026-11-23
- Branch: `develop`
- Source baseline: `ae795b8e1ceb7db44733638ec29f7309969ebdaf`
- Scope: customer registration, login/JWT, role/active-account guards, rate limiting, tests and database schema
- Release decision: **PENDING** — local test/SQL evidence not yet supplied
- This does **not** certify production security or close the separate Order Management Release Gate.

## 1. Verified from source inspection

- [x] `POST /api/auth/register` returns 201 with `RegisterResponseDto`
- [x] `POST /api/auth/login` returns an access token
- [x] Only Customer Role is assigned by `RegisterAsync`
- [x] `IPasswordHasher<User>` hashes persisted passwords
- [x] Register normalizes email with Trim + lowercase
- [x] `NormalizedEmailAttribute` validates trimmed email for Register and Login
- [x] `UX_Users_Email` unique index is declared in EF configuration
- [x] SQL unique constraint exceptions are mapped to HTTP 409
- [x] JWT token uses HS256; 15-minute lifetime; issuer/audience/signature/lifetime validation configured
- [x] JWT account and role claims are checked against current database state
- [x] Login and Register have separate per-IP fixed-window policies
- [x] DTO/service/HTTP/concurrency regression test source files exist

These are source-code observations, **not proof of successful runtime verification**.

## 2. Review finding / remediation

- [ ] Complete HTTP email-whitespace test: in `RegisterHttpTests.cs`, change the register request email to `$"  {email.ToUpperInvariant()}  "` and the Login DTO email to the same expression; rerun its test.
- [ ] Check input-validation errors returned for invalid/empty/oversized names, email, password and mismatched confirmation.
- [ ] Verify concurrent Register returns exactly one 201 and one 409, one persisted Customer; repeat at least 10 times.

## 3. Local release evidence (record date, command and result)

| Gate | Result | Evidence (date, command / sanitized log reference) |
|---|---|---|
| Build solution with warnings as errors | PENDING | |
| Authentication input-validation tests | PENDING | |
| Register DTO validation tests | PENDING | |
| Register Service tests | PENDING | |
| Register HTTP tests | PENDING | |
| Auth Login/JWT HTTP tests | PENDING | |
| Register concurrency repeated 10 times | PENDING | |
| Full solution test suite | PENDING | |
| InitialCreate migration applied to FlowerShop_Test | PENDING | |
| `UX_Users_Email` exists, unique, enabled, unfiltered | PENDING | |
| Database audit reviewed, any anomalies explained | PENDING | |

### Commands (PowerShell; run from solution root)

```powershell
$project = 'FlowerShop.IntegrationTests/FlowerShop.IntegrationTests.csproj'

dotnet build FlowerShop.slnx --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'AUTH_BUILD_FAILED' }

$filter = 'FullyQualifiedName~AuthenticationInputValidationTests|FullyQualifiedName~RegisterDtoValidationTests|FullyQualifiedName~RegisterServiceTests|FullyQualifiedName~RegisterHttpTests|FullyQualifiedName~AuthHttpTests'
dotnet test $project --no-build --filter $filter
if ($LASTEXITCODE -ne 0) { throw 'AUTH_REGRESSION_FAILED' }

for ($run = 1; $run -le 10; $run++) {
    dotnet test $project --no-build --filter 'FullyQualifiedName~ConcurrentRegistration_ShouldCreateOnlyOneCustomer'
    if ($LASTEXITCODE -ne 0) { throw "AUTH_CONCURRENCY_FAILED_RUN_$run" }
}

dotnet test FlowerShop.slnx --no-build
if ($LASTEXITCODE -ne 0) { throw 'FULL_TEST_SUITE_FAILED' }
```

Confirm `FLOWERSHOP_TEST_CONNECTION_STRING` points to `FlowerShop_Test`, `MultipleActiveResultSets=False`, and migrations are applied. The integration tests are sequential by assembly configuration. Run `scripts/authentication-integrity-audit.sql` in SSMS, connected to `FlowerShop_Test` (read-only script with an explicit DB-name guard).

**Never commit connection strings, JWT keys, credentials, full database dumps or logs containing secrets / personal data.** Record only sanitized test summaries.

## 4. Decision

- [ ] **PASS (COD MVP auth only):** All mandatory tests and SQL/schema checks pass; HTTP whitespace regression is applied; no unexplained finding remains.
- [ ] **BLOCKED:** Any reproducible build, authorization, data integrity, JWT, or regression failure remains.
- [ ] **INCONCLUSIVE:** Tests, audit, migration/index evidence, or required data is unavailable.

Decision owner: ____________________
Verification date: __________________
Result / evidence reference: __________________
Last tested commit: __________________

## 5. Backlog / non-goals for this release

- Email ownership verification and recovery flows (forgot/reset password)
- Breached-password screening and stronger anti-abuse policies
- Refresh tokens, session management and explicit token revocation
- Account deletion and PII retention policy
- Distributed rate limiting / trusted proxy IP forwarding in production
- Security review of secrets, logging, deployments, HTTPS, CORS and operational monitoring
- Existing legacy email normalization: audit before any data migration
- User Profile and Address Management (next feature module)

No broader production-ready security claim is made by this gate.
