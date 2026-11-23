-- FlowerShop Authentication: read-only audit for FlowerShop_Test only.
-- SQL Server 2022. Do NOT execute against production.
-- No passwords, hashes, email addresses, or credentials are returned.
SET NOCOUNT ON;

IF DB_NAME() <> N'FlowerShop_Test'
    THROW 51001, 'AUTH_AUDIT_REQUIRES_FLOWERSHOP_TEST', 1;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
    THROW 51002, 'USERS_TABLE_NOT_FOUND', 1;

IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51003, 'EF_MIGRATIONS_HISTORY_NOT_FOUND', 1;

-- 1. Migrations present: implementation must match the applied database.
SELECT MigrationId, ProductVersion
FROM dbo.__EFMigrationsHistory
ORDER BY MigrationId;

-- 2. Metadata check: UX_Users_Email must be unique, enabled and unfiltered.
SELECT
    i.name AS IndexName,
    i.is_unique AS IsUnique,
    i.is_disabled AS IsDisabled,
    i.has_filter AS HasFilter,
    CASE WHEN i.is_unique = 1 AND i.is_disabled = 0 AND i.has_filter = 0
         THEN 'PASS' ELSE 'FAIL' END AS AuditStatus
FROM sys.indexes AS i
INNER JOIN sys.tables AS t ON t.object_id = i.object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE s.name = N'dbo' AND t.name = N'Users'
  AND i.name = N'UX_Users_Email';
-- If the query returns no rows, report FAIL (missing index).

-- 3. Basic summary of persisted Users, without personal information.
SELECT
    COUNT_BIG(*) AS UserCount,
    COALESCE(SUM(CASE WHEN [Role] = 0 THEN CONVERT(bigint, 1) ELSE 0 END), 0) AS CustomerCount,
    COALESCE(SUM(CASE WHEN [Role] = 1 THEN CONVERT(bigint, 1) ELSE 0 END), 0) AS AdminCount,
    COALESCE(SUM(CASE WHEN [IsActive] = 0 THEN CONVERT(bigint, 1) ELSE 0 END), 0) AS DisabledCount
FROM dbo.Users;

-- 4. Invariants and potential legacy-data issues.
-- Empty database is INCONCLUSIVE for persisted-record checks.
;WITH DuplicateNormalizedEmailGroups AS
(
    SELECT LOWER(LTRIM(RTRIM([Email]))) AS EmailKey
    FROM dbo.Users
    GROUP BY LOWER(LTRIM(RTRIM([Email])))
    HAVING COUNT_BIG(*) > 1
)
SELECT
    (SELECT COUNT_BIG(*) FROM dbo.Users) AS AuditedUsers,
    (SELECT COUNT_BIG(*) FROM DuplicateNormalizedEmailGroups) AS DuplicateNormalizedEmailGroups,
    (SELECT COUNT_BIG(*) FROM dbo.Users
     WHERE [Email] IS NULL OR LEN(LTRIM(RTRIM([Email]))) = 0) AS EmptyEmailCount,
    (SELECT COUNT_BIG(*) FROM dbo.Users
     WHERE [Email] COLLATE Latin1_General_100_BIN2 <>
           LOWER(LTRIM(RTRIM([Email]))) COLLATE Latin1_General_100_BIN2) AS UnnormalizedEmailCount,
    (SELECT COUNT_BIG(*) FROM dbo.Users
     WHERE [PasswordHash] IS NULL OR LEN(LTRIM(RTRIM([PasswordHash]))) < 20) AS SuspiciousPasswordHashCount,
    (SELECT COUNT_BIG(*) FROM dbo.Users
     WHERE [Role] NOT IN (0, 1)) AS InvalidRoleCount;

-- Interpretation:
-- * DuplicateNormalizedEmailGroups > 0: FAIL / investigate uniqueness and collation.
-- * EmptyEmailCount > 0: FAIL.
-- * UnnormalizedEmailCount > 0: REVIEW legacy User records before modifying them.
-- * SuspiciousPasswordHashCount > 0: REVIEW; length is NOT proof of hashing.
-- * InvalidRoleCount > 0: FAIL.
-- * AuditedUsers = 0: INCONCLUSIVE for persisted-record checks.
-- * The database cannot establish that registrations were authorized;
--   API integration tests must cover role injection, hashing and concurrent requests.
