/*
    FlowerShop COD MVP - Order Management Integrity Audit
    Scope: FlowerShop_Test only, read-only.
    Baseline: develop e7178b94 (2026-11-23).

    IMPORTANT:
    - Only orders with a non-NULL CheckoutRequestFingerprint are
      in the new COD checkout audit scope.
    - Legacy/test-fixture orders without a fingerprint are excluded.
    - Run AFTER migrations and tests, and review the result sets.
    - No UPDATE/DELETE/INSERT operations are performed.
*/
USE [FlowerShop_Test];
GO
SET NOCOUNT ON;

IF DB_NAME() <> N'FlowerShop_Test'
    THROW 51000, 'AUDIT_REQUIRES_FlowerShop_Test', 1;

PRINT N'1. Applied EF Core migrations';
SELECT MigrationId, ProductVersion
FROM dbo.__EFMigrationsHistory
ORDER BY MigrationId;

PRINT N'2. Expected unique OrderCode index (one row expected; IsUnique = 1)';
SELECT i.name AS IndexName, i.is_unique AS IsUnique,
       i.is_disabled AS IsDisabled
FROM sys.indexes AS i
WHERE i.object_id = OBJECT_ID(N'dbo.Orders')
  AND i.name = N'UX_Orders_OrderCode';

PRINT N'3. Audited COD checkout order count (must be > 0 for meaningful data audit)';
SELECT COUNT_BIG(*) AS AuditedCheckoutOrderCount
FROM dbo.Orders
WHERE CheckoutRequestFingerprint IS NOT NULL;

PRINT N'4. Financial, item, and COD lifecycle anomalies (ZERO rows expected)';
SELECT o.Id AS OrderId,
       o.OrderCode,
       o.Status AS OrderStatus,
       p.PaymentCount,
       p.PaymentStatus,
       p.PaidAt,
       o.Subtotal,
       items.ItemsSubtotal,
       o.TotalAmount,
       p.PaymentAmount,
       problems.IntegrityError
FROM dbo.Orders AS o
CROSS APPLY
(
    SELECT COUNT_BIG(*) AS PaymentCount,
           MAX(p2.Method) AS PaymentMethod,
           MAX(p2.Status) AS PaymentStatus,
           MAX(p2.Amount) AS PaymentAmount,
           MAX(p2.Currency) AS PaymentCurrency,
           MAX(p2.PaidAt) AS PaidAt
    FROM dbo.Payments AS p2
    WHERE p2.OrderId = o.Id
) AS p
CROSS APPLY
(
    SELECT COUNT_BIG(*) AS ItemCount,
           SUM(CONVERT(decimal(28, 2), oi.UnitPrice)
               * oi.Quantity) AS ItemsSubtotal
    FROM dbo.OrderItems AS oi
    WHERE oi.OrderId = o.Id
) AS items
CROSS APPLY
(
    SELECT CASE
        WHEN o.TotalAmount <= 0 OR o.Subtotal <= 0
            THEN N'NON_POSITIVE_ORDER_AMOUNT'
        WHEN o.CheckoutRequestFingerprint IS NOT NULL
             AND LEN(o.CheckoutRequestFingerprint) <> 64
            THEN N'INVALID_REQUEST_FINGERPRINT_LENGTH'
        WHEN p.PaymentCount <> 1
            THEN N'INVALID_PAYMENT_COUNT'
        WHEN p.PaymentMethod <> 1
            THEN N'INVALID_PAYMENT_METHOD'
        WHEN p.PaymentCurrency <> 'VND'
            THEN N'INVALID_PAYMENT_CURRENCY'
        WHEN p.PaymentAmount <> o.TotalAmount
            THEN N'PAYMENT_AMOUNT_MISMATCH'
        WHEN items.ItemCount = 0
            THEN N'MISSING_ORDER_ITEMS'
        WHEN items.ItemsSubtotal <> o.Subtotal
            THEN N'ORDER_SUBTOTAL_MISMATCH'
        WHEN o.TotalAmount <> o.Subtotal + o.ShippingFee - o.DiscountAmount
            THEN N'ORDER_TOTAL_MISMATCH'
        WHEN o.Status IN (0, 1, 2)
             AND (p.PaymentStatus <> 0 OR p.PaidAt IS NOT NULL)
            THEN N'PRE_SHIPPING_PAYMENT_INVALID'
        WHEN o.Status = 3
             AND NOT (
                 (p.PaymentStatus = 0 AND p.PaidAt IS NULL) OR
                 (p.PaymentStatus = 2 AND p.PaidAt IS NOT NULL)
             )
            THEN N'SHIPPING_PAYMENT_INVALID'
        WHEN o.Status = 4
             AND (p.PaymentStatus <> 2 OR p.PaidAt IS NULL)
            THEN N'DELIVERED_WITHOUT_SETTLED_COD'
        WHEN o.Status = 5
             AND (p.PaymentStatus <> 4 OR p.PaidAt IS NOT NULL)
            THEN N'CANCELLED_ORDER_PAYMENT_INVALID'
        ELSE NULL
    END AS IntegrityError
) AS problems
WHERE o.CheckoutRequestFingerprint IS NOT NULL
  AND problems.IntegrityError IS NOT NULL
ORDER BY o.CreatedAt DESC, o.Id DESC;

PRINT N'5. Duplicate OrderCode check (ZERO rows expected)';
SELECT OrderCode, COUNT_BIG(*) AS DuplicateCount
FROM dbo.Orders
GROUP BY OrderCode
HAVING COUNT_BIG(*) > 1;

PRINT N'AUDIT COMPLETE: review ALL result sets. No changes made.';
