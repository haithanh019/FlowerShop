-- Optional plan inspection. Run on FlowerShop_Test only, while tests/API are idle.
-- In SSMS enable Include Actual Execution Plan (Ctrl+M).
-- Capture before migration and again after migration using comparable data.
SET NOCOUNT ON;
IF DB_NAME() <> N'FlowerShop_Test'
    THROW 51000, 'Use FlowerShop_Test only.', 1;

DECLARE @VoucherId uniqueidentifier, @UserId uniqueidentifier;
-- Select an example from existing test data (no data is created).
SELECT TOP (1) @VoucherId = VoucherId, @UserId = UserId
FROM dbo.Orders WHERE VoucherId IS NOT NULL
ORDER BY VoucherId, UserId, Id;
IF @VoucherId IS NULL
BEGIN
    PRINT 'No voucher-linked orders. Cannot measure a representative workload.';
    RETURN;
END;

SELECT @VoucherId AS SampleVoucherId, @UserId AS SampleUserId;
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

-- HasOrderHistoryAsync
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Orders WHERE VoucherId = @VoucherId)
    THEN 1 ELSE 0 END AS HasHistory;

-- Global usage
SELECT COUNT_BIG(*) AS GlobalUsage
FROM dbo.Orders WHERE VoucherId = @VoucherId AND Status <> 5;

-- Per-user usage
SELECT COUNT_BIG(*) AS CustomerUsage
FROM dbo.Orders WHERE VoucherId = @VoucherId AND UserId = @UserId AND Status <> 5;

-- Admin per-user limit check (sample limit of 1)
SELECT UserId FROM dbo.Orders
WHERE VoucherId = @VoucherId AND Status <> 5
GROUP BY UserId HAVING COUNT_BIG(*) > 1;

SET STATISTICS TIME OFF;
SET STATISTICS IO OFF;
