# FlowerShop — Customer Profile Management Release Gate (5.9.5)

- Review date: 2026-11-24
- Branch: `develop`
- Inspected baseline: `b57e7749a267c9869f6928411f27416562e3b5cd`
- Scope: Customer profile GET/PUT, JWT, normalization, protected fields, idempotency, cache headers, concurrency
- Decision: **PENDING** — not certified until local build, tests, SQL review and required source files are verified.

## 1. Source observations and prerequisites

- [x] Profile DTO exposes only UserId, FullName, Email, PhoneNumber, IsEmailVerified, CreatedAtUtc and UpdatedAtUtc.
- [x] Update DTO exposes only FullName and PhoneNumber; object-level validation and GeneratedRegex are present.
- [x] Profile service checks active Customer and uses no-tracking reads.
- [x] Update uses tracked property assignments and a per-user locked transaction.
- [x] Normalized no-op preserves UpdatedAt; changed timestamp matches datetime2(3).
- [ ] Verify `FlowerShop.API/Controllers/UserProfileController.cs` exists in the final working tree and commit. It was absent at the inspected baseline. A copy from 5.9.4 is supplied if needed.
- [ ] Verify `FlowerShop.IntegrationTests/Application/UserProfileUpdateServiceTests.cs` exists in the final working tree and commit. It was absent at the inspected baseline. A copy from 5.9.3 is supplied if needed.
- [ ] Verify all HTTP tests and this gate/audit are committed.

Source observations are not runtime evidence. Do not overwrite newer local implementations without reviewing the diff.

## 2. Acceptance coverage

| Scenario | Expected |
|---|---|
| GET /api/me/profile | 200, exact safe DTO JSON fields, JWT subject only |
| PUT /api/me/profile | 200, normalized editable fields persisted |
| Anonymous / malformed JWT | 401 on both endpoints |
| Admin JWT | 403 on both endpoints |
| Disabled Customer / outdated role claim | Existing JWT receives 401 |
| Invalid name/phone, missing name, malformed/null JSON | 400, no mutation |
| Extra JSON fields and spoofed query userId | Current default JSON binding ignores extras; JWT identity and protected fields unchanged |
| Customer isolation | Another Customer's record remains unchanged |
| Repeated normalized PUT | UpdatedAt unchanged |
| Blank or omitted PhoneNumber | null persisted |
| Concurrent PUT | Two successful responses; final record equals one complete payload |
| Successful GET/PUT | Cache-Control contains no-store; exact JSON contract |

Lock-timeout HTTP 409 is mapped by the existing exception handler; this gate does not deliberately hold locks until timeout. No claim of exhaustive production security testing is made.

## 3. Local release evidence

| Gate | Result | Sanitized evidence / date |
|---|---|---|
| Controller and update-service test file present | PENDING | |
| Build with warnings as errors | PENDING | |
| UserProfileDtoValidationTests (24 cases) | PENDING | |
| UserProfileQueryServiceTests (2 methods) | PENDING | |
| UserProfileUpdateServiceTests (3 methods) | PENDING | |
| UserProfileHttpTests (3 methods) | PENDING | |
| Concurrent HTTP profile update ×5 | PENDING | |
| Concurrent service profile update ×5 | PENDING | |
| Full solution regression | PENDING | |
| Current migrations applied to FlowerShop_Test | PENDING | |
| Users column metadata and email unique index match expected | PENDING | |
| SQL data anomalies reviewed; no unexplained finding | PENDING | |

## 4. Commands (PowerShell, solution root)

Use `FLOWERSHOP_TEST_CONNECTION_STRING` with database `FlowerShop_Test`, MARS disabled. Confirm files are present before testing; a filter with no matching tests does not count as passing.

```powershell
$project = 'FlowerShop.IntegrationTests/FlowerShop.IntegrationTests.csproj'
$required = @('FlowerShop.API/Controllers/UserProfileController.cs',
              'FlowerShop.IntegrationTests/Application/UserProfileUpdateServiceTests.cs',
              'FlowerShop.IntegrationTests/Application/UserProfileHttpTests.cs')
foreach ($file in $required) {
    if (-not (Test-Path $file)) { throw "MISSING_FILE: $file" }
}

dotnet build FlowerShop.slnx --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'PROFILE_BUILD_FAILED' }

dotnet test $project --no-build --filter 'FullyQualifiedName~UserProfileHttpTests'
if ($LASTEXITCODE -ne 0) { throw 'PROFILE_HTTP_FAILED' }

dotnet test $project --no-build --filter 'FullyQualifiedName~UserProfile'
if ($LASTEXITCODE -ne 0) { throw 'PROFILE_REGRESSION_FAILED' }

for ($i = 1; $i -le 5; $i++) {
    dotnet test $project --no-build --filter 'FullyQualifiedName~ConcurrentHttpProfileUpdate_ShouldPersistOneCompletePayload|FullyQualifiedName~ConcurrentProfileUpdate_ShouldPersistOneCompletePayload'
    if ($LASTEXITCODE -ne 0) { throw "PROFILE_CONCURRENCY_FAILED_$i" }
}

dotnet test FlowerShop.slnx --no-build
if ($LASTEXITCODE -ne 0) { throw 'FULL_SUITE_FAILED' }
```

Expected from the supplied Profile files: 32 cases (24 + 2 + 3 + 3), including the HTTP tests. Verify actual test counts and no skipped cases. Each concurrency repetition should discover two tests.

Run `scripts/customer-profile-integrity-audit.sql` in SSMS against `FlowerShop_Test`. It is read-only and returns schema metadata and aggregate counts, not customer PII. Review zero-row/zero-customer results in context: if no Customers exist, data audit alone is inconclusive; use seeded validated test evidence or a dedicated test Customer. The SQL checks do not exactly replicate .NET Unicode whitespace handling and cannot establish HTTP authorization, idempotency or protected field immutability. Use HTTP and service tests for those guarantees.

## 5. Release decision

- [ ] PASS (Customer Profile module only): all mandatory tests and schema/data checks pass; no unexplained finding remains.
- [ ] BLOCKED: a reproducible build, test, authorization or data integrity failure remains.
- [ ] INCONCLUSIVE: mandatory evidence is missing.

Verified by: ____________________
Date/time: _______________________
Final tested commit: ______________
Sanitized evidence: _______________

## 6. Non-goals and follow-up

Email change/verification, password change/recovery, refresh tokens, account deletion, frontend integration and production security review are separate work. Reconcile pending authentication, order and address gates before declaring the complete backend ready.

Never commit connection strings, JWT keys, real credentials or unsanitized logs containing customer data.
