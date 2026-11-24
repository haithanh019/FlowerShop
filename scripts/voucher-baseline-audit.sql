-- FlowerShop 5.10.1: read-only baseline audit. No data/schema changes.
SET NOCOUNT ON;
IF DB_NAME() <> N'FlowerShop_Test'
    THROW 51000, 'Run only against FlowerShop_Test.', 1;
IF OBJECT_ID(N'dbo.Vouchers', N'U') IS NULL OR OBJECT_ID(N'dbo.Orders', N'U') IS NULL
    THROW 51001, 'Required tables are missing.', 1;

-- 1. Column metadata: verify against current EF configuration.
SELECT OBJECT_NAME(c.object_id) AS TableName, c.name AS ColumnName,
       TYPE_NAME(c.user_type_id) AS SqlType, c.max_length AS MaxBytes,
       c.precision, c.scale, c.is_nullable
FROM sys.columns c
WHERE c.object_id = OBJECT_ID(N'dbo.Vouchers')
   OR (c.object_id = OBJECT_ID(N'dbo.Orders') AND c.name IN
       (N'VoucherId', N'UserId', N'Status', N'Subtotal', N'ShippingFee', N'DiscountAmount', N'TotalAmount'))
ORDER BY TableName, c.column_id;

-- 2. Check constraints: inspect definitions, enabled/trusted state.
SELECT OBJECT_NAME(parent_object_id) AS TableName, name, definition,
       is_disabled, is_not_trusted
FROM sys.check_constraints
WHERE parent_object_id IN (OBJECT_ID(N'dbo.Vouchers'), OBJECT_ID(N'dbo.Orders'))
ORDER BY TableName, name;

-- 3. Index columns in order (including key ordinal and filter).
SELECT OBJECT_NAME(i.object_id) AS TableName, i.name, i.is_unique,
       i.is_disabled, i.has_filter, i.filter_definition,
       ic.key_ordinal, ic.is_included_column, c.name AS ColumnName
FROM sys.indexes i
JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id IN (OBJECT_ID(N'dbo.Vouchers'), OBJECT_ID(N'dbo.Orders'))
ORDER BY TableName, i.name, ic.key_ordinal, ic.index_column_id;

SELECT fk.name, fk.is_disabled, fk.is_not_trusted, fk.delete_referential_action_desc,
       pc.name AS ParentColumn, rc.name AS ReferencedColumn
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE fk.parent_object_id = OBJECT_ID(N'dbo.Orders')
  AND fk.referenced_object_id = OBJECT_ID(N'dbo.Vouchers');

-- 4. Aggregate counts (no codes or customer data emitted).
SELECT COUNT_BIG(*) AS VoucherCount,
       COUNT_BIG(CASE WHEN IsActive = 1 THEN 1 END) AS ActiveFlagCount,
       COUNT_BIG(CASE WHEN UsedCount < 0 OR (UsageLimit IS NOT NULL AND UsedCount > UsageLimit) THEN 1 END) AS InvalidCounterCount,
       COUNT_BIG(CASE WHEN DiscountType = 2 AND MaxDiscountAmount IS NOT NULL THEN 1 END) AS FixedWithCapCount,
       COUNT_BIG(CASE WHEN DATALENGTH(Code) = 0 OR DATALENGTH(Code) <> DATALENGTH(TRIM(Code))
           OR Code COLLATE Latin1_General_100_BIN2 <> UPPER(Code) COLLATE Latin1_General_100_BIN2
           OR Code COLLATE Latin1_General_100_BIN2 LIKE N'%[^A-Z0-9_-]%'
           THEN 1 END) AS NoncanonicalCodeCount
FROM dbo.Vouchers;

SELECT COUNT_BIG(*) AS NormalizedCodeCollisionGroups
FROM (SELECT UPPER(TRIM(Code)) COLLATE Latin1_General_100_BIN2 AS NormalizedCode
      FROM dbo.Vouchers GROUP BY UPPER(TRIM(Code)) COLLATE Latin1_General_100_BIN2
      HAVING COUNT_BIG(*) > 1) c;

-- 5. Compare with proposed non-cancelled usage semantics. Investigate legacy mismatches.
WITH UsageByVoucher AS (
    SELECT VoucherId, COUNT_BIG(*) AS ActualCount
    FROM dbo.Orders WHERE VoucherId IS NOT NULL AND Status <> 5
    GROUP BY VoucherId
)
SELECT COUNT_BIG(*) AS VoucherCount,
       COUNT_BIG(CASE WHEN CONVERT(bigint, v.UsedCount) <> COALESCE(u.ActualCount, 0) THEN 1 END) AS CounterMismatchCount
FROM dbo.Vouchers v LEFT JOIN UsageByVoucher u ON u.VoucherId = v.Id;

WITH UsageByCustomer AS (
    SELECT VoucherId, UserId, COUNT_BIG(*) AS ActualCount
    FROM dbo.Orders WHERE VoucherId IS NOT NULL AND Status <> 5
    GROUP BY VoucherId, UserId
)
SELECT COUNT_BIG(*) AS PerUserLimitViolationGroups
FROM UsageByCustomer u JOIN dbo.Vouchers v ON v.Id = u.VoucherId
WHERE v.PerUserLimit IS NOT NULL AND u.ActualCount > v.PerUserLimit;

SELECT COUNT_BIG(*) AS VoucherOrderCount,
       COUNT_BIG(CASE WHEN Status = 5 THEN 1 END) AS CancelledVoucherOrderCount,
       COUNT_BIG(CASE WHEN DiscountAmount <= 0 OR DiscountAmount > Subtotal OR TotalAmount <= 0
         OR TotalAmount <> Subtotal + ShippingFee - DiscountAmount THEN 1 END) AS VoucherFinancialAnomalyCount
FROM dbo.Orders WHERE VoucherId IS NOT NULL;

SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;
-- These counts are baseline observations, not release certification.
