-- FlowerShop Customer Address Management integrity audit.
-- READ ONLY. Execute only against FlowerShop_Test.
SET NOCOUNT ON;

IF DB_NAME() <> N'FlowerShop_Test'
BEGIN
    THROW 51001, 'Customer Address audit is restricted to FlowerShop_Test.', 1;
END;

-- 1. Expected filtered unique index.
SELECT
    i.name AS IndexName,
    i.is_unique AS IsUnique,
    i.is_disabled AS IsDisabled,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDefinition
FROM sys.indexes AS i
WHERE i.object_id = OBJECT_ID(N'dbo.UserAddresses')
  AND i.name = N'UX_UserAddresses_OneDefault';

-- 2. Zero rows expected: Customers with more than one default address.
SELECT
    ua.UserId,
    COUNT_BIG(*) AS DefaultCount
FROM dbo.UserAddresses AS ua
WHERE ua.IsDefault = 1
GROUP BY ua.UserId
HAVING COUNT_BIG(*) > 1;

-- 3. Review: Customers who have addresses, but no default.
--    Zero rows are expected for data maintained exclusively through the service.
SELECT
    ua.UserId,
    COUNT_BIG(*) AS AddressCount
FROM dbo.UserAddresses AS ua
GROUP BY ua.UserId
HAVING SUM(CASE WHEN ua.IsDefault = 1 THEN 1 ELSE 0 END) = 0;

-- 4. Review: invalid country code values in persisted addresses.
-- SQL Server's default collation may be case-insensitive; use a binary collation.
SELECT ua.Id, ua.UserId, ua.CountryCode
FROM dbo.UserAddresses AS ua
WHERE LEN(ua.CountryCode) <> 2
   OR ua.CountryCode COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Z]%';

-- 5. Summary. Empty database is not proof of end-to-end correctness.
SELECT
    COUNT_BIG(*) AS AddressCount,
    SUM(CASE WHEN IsDefault = 1 THEN CONVERT(bigint, 1)
             ELSE CONVERT(bigint, 0) END) AS DefaultAddressCount
FROM dbo.UserAddresses;
