-- 5.10.8.E. Read-only audit. Run in SSMS on FlowerShop_Test while API/test writers are idle.
SET NOCOUNT ON;
IF DB_NAME() <> N'FlowerShop_Test'
    THROW 51010, 'Run only against FlowerShop_Test.', 1;
IF OBJECT_ID(N'dbo.Vouchers', N'U') IS NULL OR OBJECT_ID(N'dbo.Orders', N'U') IS NULL
   OR OBJECT_ID(N'dbo.OrderItems', N'U') IS NULL OR OBJECT_ID(N'dbo.Payments', N'U') IS NULL
   OR OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51011, 'Required tables missing.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory
    WHERE MigrationId = N'20261124130728_AddVoucherUsageCompositeIndex')
    THROW 51012, 'Voucher index migration missing.', 1;

DECLARE @checks TABLE (CheckName nvarchar(100) NOT NULL, ViolationCount bigint NOT NULL);

INSERT @checks
SELECT N'Noncanonical code', COUNT_BIG(*) FROM dbo.Vouchers AS v
WHERE DATALENGTH(v.Code) NOT BETWEEN 2 AND 100
   OR CONVERT(varbinary(100), TRANSLATE(v.Code COLLATE Latin1_General_100_BIN2,
       N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-', REPLICATE(N'A',38)))
      <> CONVERT(varbinary(100), REPLICATE(N'A', DATALENGTH(v.Code)/2));

INSERT @checks
SELECT N'Invalid voucher terms or counters', COUNT_BIG(*) FROM dbo.Vouchers
WHERE DiscountType NOT IN (1,2) OR DiscountValue <= 0
   OR (DiscountType = 1 AND DiscountValue > 100) OR MinOrderAmount < 0
   OR (MaxDiscountAmount IS NOT NULL AND (DiscountType <> 1 OR MaxDiscountAmount <= 0))
   OR UsageLimit <= 0 OR PerUserLimit <= 0 OR UsedCount < 0 OR UsedCount > UsageLimit
   OR StartAt >= EndAt;

INSERT @checks
SELECT N'Global usage mismatch', COUNT_BIG(*) FROM dbo.Vouchers AS v
OUTER APPLY (SELECT COUNT_BIG(*) AS ActualCount FROM dbo.Orders AS o
             WHERE o.VoucherId = v.Id AND o.Status <> 5) AS u
WHERE v.UsedCount <> u.ActualCount;

INSERT @checks
SELECT N'Per-user usage above limit', COUNT_BIG(*)
FROM (SELECT VoucherId, UserId, COUNT_BIG(*) AS ActualCount FROM dbo.Orders
      WHERE VoucherId IS NOT NULL AND Status <> 5 GROUP BY VoucherId, UserId) AS u
JOIN dbo.Vouchers AS v ON v.Id = u.VoucherId
WHERE v.PerUserLimit IS NOT NULL AND u.ActualCount > v.PerUserLimit;

-- Scope: voucher-linked orders, including cancelled history.
INSERT @checks
SELECT N'Invalid linked order financial snapshots', COUNT_BIG(*) FROM dbo.Orders AS o
WHERE o.VoucherId IS NOT NULL AND
    (o.Subtotal <= 0 OR o.ShippingFee < 0 OR o.DiscountAmount <= 0
     OR o.DiscountAmount > o.Subtotal OR o.TotalAmount <= 0
     OR o.TotalAmount <> o.Subtotal + o.ShippingFee - o.DiscountAmount);

-- For the agreed COD/one-voucher MVP, a discount without a link is anomalous.
INSERT @checks
SELECT N'Discount without voucher link', COUNT_BIG(*) FROM dbo.Orders
WHERE VoucherId IS NULL AND DiscountAmount <> 0;

INSERT @checks
SELECT N'Invalid linked order items', COUNT_BIG(*) FROM dbo.Orders AS o
OUTER APPLY (SELECT COUNT_BIG(*) AS ItemCount, COUNT_BIG(DISTINCT i.ProductId) AS ProductCount,
                    SUM(CONVERT(decimal(28,2), i.UnitPrice * i.Quantity)) AS ActualSubtotal,
                    COUNT_BIG(CASE WHEN i.Quantity <= 0 OR i.UnitPrice <= 0 THEN 1 END) AS InvalidCount
             FROM dbo.OrderItems AS i WHERE i.OrderId = o.Id) AS l
WHERE o.VoucherId IS NOT NULL AND (l.ItemCount = 0 OR l.ItemCount <> l.ProductCount
      OR l.InvalidCount > 0 OR l.ActualSubtotal <> o.Subtotal);

-- PaymentStatus: Pending=0, Paid=2, Cancelled=4; OrderStatus: Shipping=3, Delivered=4, Cancelled=5.
-- Shipping may be unpaid or paid; Delivered must have a paid COD payment.
INSERT @checks
SELECT N'Invalid linked COD payment snapshots or lifecycle', COUNT_BIG(*) FROM dbo.Orders AS o
OUTER APPLY (SELECT COUNT_BIG(*) AS PaymentCount FROM dbo.Payments AS p
             WHERE p.OrderId = o.Id) AS pay
OUTER APPLY (SELECT COUNT_BIG(*) AS ValidCount FROM dbo.Payments AS p
             WHERE p.OrderId = o.Id
               AND p.Method = 1 AND p.Currency COLLATE Latin1_General_100_BIN2 = 'VND'
               AND p.Amount = o.TotalAmount AND p.Amount > 0
               AND ((o.Status IN (0,1,2) AND p.Status = 0 AND p.PaidAt IS NULL)
                 OR (o.Status = 3 AND ((p.Status = 0 AND p.PaidAt IS NULL) OR (p.Status = 2 AND p.PaidAt IS NOT NULL)))
                 OR (o.Status = 4 AND p.Status = 2 AND p.PaidAt IS NOT NULL)
                 OR (o.Status = 5 AND p.Status = 4 AND p.PaidAt IS NULL))) AS valid
WHERE o.VoucherId IS NOT NULL AND (pay.PaymentCount <> 1 OR valid.ValidCount <> 1);

INSERT @checks
SELECT N'Voucher FK missing or untrusted', CASE WHEN EXISTS (
    SELECT 1 FROM sys.foreign_keys AS fk
    JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
    JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
    JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.Orders') AND fk.referenced_object_id = OBJECT_ID(N'dbo.Vouchers')
      AND pc.name = N'VoucherId' AND rc.name = N'Id' AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
      AND fk.delete_referential_action = 0) THEN 0 ELSE 1 END;

INSERT @checks
SELECT N'Disabled or untrusted check constraints', COUNT_BIG(*) FROM sys.check_constraints
WHERE parent_object_id IN (OBJECT_ID(N'dbo.Vouchers'), OBJECT_ID(N'dbo.Orders'),
    OBJECT_ID(N'dbo.OrderItems'), OBJECT_ID(N'dbo.Payments')) AND (is_disabled = 1 OR is_not_trusted = 1);

SELECT CheckName, ViolationCount, CASE WHEN ViolationCount = 0 THEN N'PASS' ELSE N'FAIL' END AS Result
FROM @checks ORDER BY CheckName;
IF EXISTS (SELECT 1 FROM @checks WHERE ViolationCount <> 0)
    THROW 51013, 'Voucher acceptance audit failed. Investigate anomalies; do not auto-repair.', 1;
SELECT N'PASS' AS VoucherLifecycleDataAudit, DB_NAME() AS DatabaseName;
