-- 5.10.8.C: read-only acceptance, run after the EF migration on FlowerShop_Test.
-- Run while the test suite and API are idle. No DDL or DML is executed.
SET NOCOUNT ON;
IF DB_NAME() <> N'FlowerShop_Test'
    THROW 51000, 'Use FlowerShop_Test only.', 1;

DECLARE @OrdersId int = OBJECT_ID(N'dbo.Orders', N'U');
IF @OrdersId IS NULL OR OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51001, 'Orders or migration history missing.', 1;

SELECT MigrationId, ProductVersion
FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory
    WHERE RIGHT(MigrationId, LEN(N'_AddVoucherUsageCompositeIndex')) = N'_AddVoucherUsageCompositeIndex')
    THROW 51002, 'AddVoucherUsageCompositeIndex migration is not applied.', 1;

-- Display actual key columns and their order, not just index names.
SELECT i.name AS IndexName, i.type_desc, i.is_unique, i.is_disabled,
       i.has_filter, i.filter_definition, ic.key_ordinal,
       c.name AS ColumnName, ic.is_descending_key, ic.is_included_column
FROM sys.indexes AS i
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = @OrdersId
  AND i.name IN (N'IX_Orders_VoucherId', N'IX_Orders_VoucherId_UserId_Status')
ORDER BY i.name, ic.key_ordinal, ic.index_column_id;

DECLARE @IndexId int = (SELECT index_id FROM sys.indexes
    WHERE object_id = @OrdersId AND name = N'IX_Orders_VoucherId_UserId_Status'
      AND type = 2 AND is_unique = 0 AND is_disabled = 0
      AND is_hypothetical = 0 AND has_filter = 0);
IF @IndexId IS NULL
    THROW 51003, 'Expected enabled, nonunique, unfiltered nonclustered index missing.', 1;

IF (SELECT COUNT(*) FROM sys.index_columns
    WHERE object_id = @OrdersId AND index_id = @IndexId AND key_ordinal > 0) <> 3
    THROW 51004, 'Unexpected composite index key count.', 1;

IF (SELECT COUNT(*) FROM sys.index_columns AS ic
    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = @OrdersId AND ic.index_id = @IndexId
      AND ic.is_descending_key = 0 AND ic.is_included_column = 0
      AND ((ic.key_ordinal = 1 AND c.name = N'VoucherId')
        OR (ic.key_ordinal = 2 AND c.name = N'UserId')
        OR (ic.key_ordinal = 3 AND c.name = N'Status'))) <> 3
    THROW 51005, 'Unexpected composite index key order.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = @OrdersId AND name = N'IX_Orders_VoucherId'
      AND type = 2 AND is_disabled = 0)
    THROW 51006, 'Original VoucherId index must be retained.', 1;

SELECT N'PASS' AS SchemaResult, DB_NAME() AS DatabaseName;

-- Independent, read-only data audit. Zero rows is expected in a clean database.
-- Status 5 = Cancelled, verified in the Domain enum.
SELECT v.Id AS VoucherId, v.Code, v.UsedCount,
       COUNT_BIG(o.Id) AS ActualUsedCount
FROM dbo.Vouchers AS v
LEFT JOIN dbo.Orders AS o ON o.VoucherId = v.Id AND o.Status <> 5
GROUP BY v.Id, v.Code, v.UsedCount
HAVING COUNT_BIG(o.Id) <> v.UsedCount;

-- Anomalous historical voucher-linked financial records; do not auto-repair.
SELECT Id AS OrderId, VoucherId, Status, DiscountAmount
FROM dbo.Orders
WHERE VoucherId IS NOT NULL AND DiscountAmount <= 0;
