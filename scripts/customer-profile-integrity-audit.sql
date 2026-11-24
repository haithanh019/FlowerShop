-- FlowerShop Customer Profile integrity audit. READ ONLY.
-- Only aggregate data and schema metadata; no emails, phone numbers or password hashes.
SET NOCOUNT ON;
IF DB_NAME() <> N'FlowerShop_Test'
BEGIN
    THROW 51001, 'Customer Profile audit is restricted to FlowerShop_Test.', 1;
END;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    THROW 51002, 'Users table is missing.', 1;
END;

-- 1. Review required column definitions. nvarchar max_length is in bytes.
-- FullName: 300 bytes, not nullable. PhoneNumber: 40 bytes, nullable.
-- CreatedAt and UpdatedAt: datetime2 scale 3; only UpdatedAt is nullable.
SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType,
       c.max_length AS MaxLengthBytes, c.is_nullable AS IsNullable,
       c.scale AS Scale
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'dbo.Users')
  AND c.name IN (N'FullName', N'PhoneNumber', N'CreatedAt', N'UpdatedAt')
ORDER BY c.name;

-- 2. Aggregate Customer data checks (Role=0).
-- SQL LTRIM/RTRIM remove ordinary spaces, not every .NET whitespace character.
-- These are conservative checks, not an exact substitute for DTO validation.
;WITH profiles AS
(
    SELECT FullName, PhoneNumber, CreatedAt, UpdatedAt,
           LTRIM(RTRIM(FullName)) AS TrimmedName,
           LTRIM(RTRIM(PhoneNumber)) AS TrimmedPhone
    FROM dbo.Users
    WHERE Role = 0
), checks AS
(
    SELECT *,
        CASE WHEN LEFT(TrimmedPhone, 1) = N'+'
             THEN SUBSTRING(TrimmedPhone, 2, 20)
             ELSE TrimmedPhone END AS PhoneBody
    FROM profiles
)
SELECT COUNT_BIG(*) AS CustomerCount,
       COUNT_BIG(CASE WHEN FullName IS NULL OR DATALENGTH(TrimmedName) / 2 NOT BETWEEN 2 AND 150
                      THEN 1 END) AS InvalidNameLengthCount,
       COUNT_BIG(CASE WHEN DATALENGTH(FullName) <> DATALENGTH(TrimmedName)
                      THEN 1 END) AS UntrimmedNameCount,
       COUNT_BIG(CASE WHEN PhoneNumber IS NOT NULL AND DATALENGTH(TrimmedPhone) = 0
                      THEN 1 END) AS BlankPhoneCount,
       COUNT_BIG(CASE WHEN DATALENGTH(PhoneNumber) <> DATALENGTH(TrimmedPhone)
                      THEN 1 END) AS UntrimmedPhoneCount,
       COUNT_BIG(CASE WHEN PhoneNumber IS NOT NULL AND (
                      DATALENGTH(TrimmedPhone) / 2 > 20
                      OR PhoneBody COLLATE Latin1_General_100_BIN2 NOT LIKE N'[0-9]%'
                      OR PhoneBody COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9 ().-]%'
                      OR (DATALENGTH(PhoneBody) - DATALENGTH(REPLACE(
                          TRANSLATE(PhoneBody, N'0123456789', N'0000000000'), N'0', N''))) / 2 < 7
                      ) THEN 1 END) AS InvalidPhoneFormatCount,
       COUNT_BIG(CASE WHEN UpdatedAt < CreatedAt THEN 1 END) AS UpdatedBeforeCreatedCount
FROM checks;

-- 3. Verify the existing email unique index and its key columns (schema only).
SELECT i.name AS IndexName, i.is_unique AS IsUnique,
       i.is_disabled AS IsDisabled, i.has_filter AS HasFilter,
       c.name AS KeyColumn, ic.key_ordinal AS KeyOrdinal
FROM sys.indexes AS i
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.Users')
  AND i.name = N'UX_Users_Email' AND ic.key_ordinal > 0
ORDER BY ic.key_ordinal;

-- Expected anomaly counts: zero for data maintained through validated flows.
-- Legacy/seed data may differ; investigate rather than automatically mutate it.
-- CustomerCount=0 is not positive data-audit evidence. HTTP/DB assertions still
-- remain mandatory. This script cannot prove authorization or mass-assignment safety.
