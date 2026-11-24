using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class AdminVoucherQueryServiceTests
{
    private static readonly DateTime Now =
        new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Query_ShouldFilterEveryStatusAtExactBoundariesWithoutTracking()
    {
        using var baseFactory = new TestApiFactory(GetTestConnection());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock(Now));
            }));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAdminVoucherQueryService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var admin = NewUser(hasher, UserRole.Admin);
        var tag = $"VQ-{Guid.NewGuid():N}".ToUpperInvariant();

        // Isolated fixtures intentionally exercise status conditions without order history.
        var inactive = NewVoucher(tag + "-A");
        inactive.IsActive = false;
        inactive.StartAt = Now.AddDays(1);
        inactive.EndAt = Now.AddDays(2);
        var upcoming = NewVoucher(tag + "-B");
        upcoming.StartAt = Now.AddMilliseconds(1);
        upcoming.UsageLimit = 1;
        upcoming.UsedCount = 1;
        var available = NewVoucher(tag + "-C");
        available.StartAt = Now; // inclusive
        available.UsageLimit = 2;
        available.UsedCount = 1;
        var expired = NewVoucher(tag + "-D");
        expired.EndAt = Now; // exclusive
        expired.UsageLimit = 1;
        expired.UsedCount = 1;
        var exhausted = NewVoucher(tag + "-E");
        exhausted.UsageLimit = 1;
        exhausted.UsedCount = 1;
        var unlimited = NewVoucher(tag + "-F");
        unlimited.UsageLimit = null;
        var maxCounter = NewVoucher(tag + "-G");
        maxCounter.UsedCount = int.MaxValue;
        var fixtures = new (Voucher Voucher, VoucherAvailabilityStatus Status)[]
        {
            (inactive, VoucherAvailabilityStatus.Inactive),
            (upcoming, VoucherAvailabilityStatus.Upcoming),
            (available, VoucherAvailabilityStatus.Available),
            (expired, VoucherAvailabilityStatus.Expired),
            (exhausted, VoucherAvailabilityStatus.Exhausted),
            (unlimited, VoucherAvailabilityStatus.Available),
            (maxCounter, VoucherAvailabilityStatus.Exhausted)
        };

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Users.Add(admin);
            db.Vouchers.AddRange(fixtures.Select(x => x.Voucher));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            foreach (var status in Enum.GetValues<VoucherAvailabilityStatus>())
            {
                var expected = fixtures.Where(x => x.Status == status)
                    .Select(x => x.Voucher.Code).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var result = await service.GetPageAsync(admin.Id,
                    codeKeyword: tag.ToLowerInvariant(), status: status,
                    sort: VoucherSortOrder.CodeAscending);
                Assert.Equal(expected.Length, result.TotalCount);
                Assert.Equal(expected, result.Items.Select(x => x.Voucher.Code).ToArray());
                Assert.All(result.Items, x =>
                {
                    Assert.Equal(status, x.Status);
                    Assert.Equal(Now, x.EvaluatedAtUtc);
                });
                Assert.Equal(Now, result.EvaluatedAtUtc);
                Assert.Equal(DateTimeKind.Utc, result.EvaluatedAtUtc.Kind);
            }

            foreach (var (voucher, status) in fixtures)
            {
                var result = await service.GetDetailAsync(admin.Id, voucher.Id);
                Assert.Equal(status, result.Status);
                Assert.Equal(voucher.Id, result.Voucher.VoucherId);
                Assert.Equal(voucher.Code, result.Voucher.Code);
                Assert.Equal(voucher.DiscountType, result.Voucher.DiscountType);
                Assert.Equal(voucher.DiscountValue, result.Voucher.DiscountValue);
                Assert.Equal(voucher.UsageLimit, result.Voucher.UsageLimit);
                Assert.Equal(voucher.UsedCount, result.Voucher.UsedCount);
                Assert.Equal(voucher.IsActive, result.Voucher.IsActive);
                Assert.Equal(voucher.StartAt, result.Voucher.StartAtUtc);
                Assert.Equal(voucher.EndAt, result.Voucher.EndAtUtc);
                Assert.Equal(DateTimeKind.Utc, result.Voucher.StartAtUtc.Kind);
                Assert.Equal(DateTimeKind.Utc, result.Voucher.CreatedAtUtc.Kind);
                Assert.Null(result.Voucher.UpdatedAtUtc);
            }

            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Query_ShouldCombineFiltersAndReturnStablePages()
    {
        using var baseFactory = new TestApiFactory(GetTestConnection());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock(Now));
            }));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAdminVoucherQueryService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var admin = NewUser(hasher, UserRole.Admin);
        var tag = $"VQ-{Guid.NewGuid():N}".ToUpperInvariant();
        var a = NewVoucher(tag + "-A");
        var b = NewVoucher(tag + "-B");
        var c = NewVoucher(tag + "-C");
        a.CreatedAt = b.CreatedAt = Now.AddDays(-1);
        c.CreatedAt = Now.AddDays(-2);
        var fixedAmount = NewVoucher(tag + "-D");
        fixedAmount.DiscountType = DiscountType.FixedAmount;
        fixedAmount.DiscountValue = 10000m;
        var expired = NewVoucher(tag + "-E");
        expired.EndAt = Now;
        var unrelated = NewVoucher($"OTHER-{Guid.NewGuid():N}".ToUpperInvariant());

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Users.Add(admin);
            db.Vouchers.AddRange(a, b, c, fixedAmount, expired, unrelated);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var first = await service.GetPageAsync(admin.Id, $"  {tag.ToLowerInvariant()}  ",
                DiscountType.Percentage, VoucherAvailabilityStatus.Available,
                VoucherSortOrder.Newest, page: 1, pageSize: 2);
            var second = await service.GetPageAsync(admin.Id, tag,
                DiscountType.Percentage, VoucherAvailabilityStatus.Available,
                VoucherSortOrder.Newest, page: 2, pageSize: 2);
            Assert.Equal(3, first.TotalCount);
            Assert.Equal(2, first.TotalPages);
            Assert.Equal(1, first.Page);
            Assert.Equal(2, first.PageSize);
            Assert.Equal(new[] { a.Id, b.Id }, first.Items.Select(x => x.Voucher.VoucherId).ToArray());
            Assert.Equal(c.Id, Assert.Single(second.Items).Voucher.VoucherId);
            Assert.Equal(3, second.TotalCount);
            Assert.Equal(2, second.TotalPages);

            var older = await service.GetPageAsync(admin.Id, tag,
                DiscountType.Percentage, VoucherAvailabilityStatus.Available,
                VoucherSortOrder.Oldest);
            Assert.Equal(new[] { c.Id, a.Id, b.Id }, older.Items.Select(x => x.Voucher.VoucherId).ToArray());
            var descending = await service.GetPageAsync(admin.Id, tag,
                DiscountType.Percentage, VoucherAvailabilityStatus.Available,
                VoucherSortOrder.CodeDescending);
            Assert.Equal(new[] { c.Id, b.Id, a.Id }, descending.Items.Select(x => x.Voucher.VoucherId).ToArray());

            var fixedPage = await service.GetPageAsync(admin.Id, tag, DiscountType.FixedAmount);
            Assert.Equal(fixedAmount.Id, Assert.Single(fixedPage.Items).Voucher.VoucherId);
            var pastEnd = await service.GetPageAsync(admin.Id, tag,
                DiscountType.Percentage, VoucherAvailabilityStatus.Available,
                page: 3, pageSize: 2);
            Assert.Empty(pastEnd.Items);
            Assert.Equal(3, pastEnd.TotalCount);
            Assert.Equal(2, pastEnd.TotalPages);
            var empty = await service.GetPageAsync(admin.Id, $"MISSING-{Guid.NewGuid():N}");
            Assert.Empty(empty.Items);
            Assert.Equal(0, empty.TotalCount);
            Assert.Equal(0, empty.TotalPages);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Query_ShouldEnforceAdminAccessAndValidateParameters()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAdminVoucherQueryService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var admin = NewUser(hasher, UserRole.Admin);
        var customer = NewUser(hasher, UserRole.Customer);
        var inactive = NewUser(hasher, UserRole.Admin);
        inactive.IsActive = false;
        var voucher = NewVoucher($"VQ-{Guid.NewGuid():N}".ToUpperInvariant());

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Users.AddRange(admin, customer, inactive);
            db.Vouchers.Add(voucher);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            foreach (var deniedId in new[] { customer.Id, inactive.Id, Guid.NewGuid() })
            {
                var denied = await Assert.ThrowsAsync<BusinessException>(
                    () => service.GetPageAsync(deniedId));
                Assert.Equal(BusinessErrorType.Unauthorized, denied.ErrorType);
                Assert.Equal("ADMIN_ACCESS_REQUIRED", denied.Message);
                var deniedDetail = await Assert.ThrowsAsync<BusinessException>(
                    () => service.GetDetailAsync(deniedId, voucher.Id));
                Assert.Equal(BusinessErrorType.Unauthorized, deniedDetail.ErrorType);
                Assert.Equal("ADMIN_ACCESS_REQUIRED", deniedDetail.Message);
            }

            await ExpectValidationAsync(() => service.GetPageAsync(Guid.Empty), "ADMIN_USER_ID_REQUIRED");
            await ExpectValidationAsync(() => service.GetDetailAsync(Guid.Empty, voucher.Id), "ADMIN_USER_ID_REQUIRED");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, page: 0), "PAGE_MUST_BE_POSITIVE");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, pageSize: 0), "PAGE_SIZE_OUT_OF_RANGE");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, pageSize: 51), "PAGE_SIZE_OUT_OF_RANGE");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, page: int.MaxValue, pageSize: 50), "PAGE_OUT_OF_RANGE");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, discountType: (DiscountType)99), "INVALID_DISCOUNT_TYPE");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, status: (VoucherAvailabilityStatus)99), "INVALID_VOUCHER_STATUS");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, sort: (VoucherSortOrder)99), "INVALID_VOUCHER_SORT");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, codeKeyword: new string('A', 51)), "VOUCHER_CODE_TOO_LONG");
            await ExpectValidationAsync(() => service.GetPageAsync(admin.Id, codeKeyword: "a b"), "VOUCHER_CODE_INVALID");
            await ExpectValidationAsync(() => service.GetDetailAsync(admin.Id, Guid.Empty), "VOUCHER_ID_REQUIRED");

            var missing = await Assert.ThrowsAsync<BusinessException>(
                () => service.GetDetailAsync(admin.Id, Guid.NewGuid()));
            Assert.Equal(BusinessErrorType.NotFound, missing.ErrorType);
            Assert.Equal("VOUCHER_NOT_FOUND", missing.Message);
            Assert.Equal(voucher.Id, (await service.GetDetailAsync(admin.Id, voucher.Id)).Voucher.VoucherId);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    private static async Task ExpectValidationAsync(Func<Task> action, string errorCode)
    {
        var error = await Assert.ThrowsAsync<ValidationException>(action);
        Assert.Equal(errorCode, error.Message);
    }

    private static Voucher NewVoucher(string code) => new()
    {
        Code = code,
        Description = "Voucher query fixture",
        DiscountType = DiscountType.Percentage,
        DiscountValue = 20m,
        MinOrderAmount = 100000m,
        PerUserLimit = 1,
        StartAt = Now.AddDays(-1),
        EndAt = Now.AddDays(1),
        IsActive = true,
        CreatedAt = Now.AddDays(-3)
    };

    private static User NewUser(IPasswordHasher<User> hasher, UserRole role)
    {
        var user = new User
        {
            FullName = "Voucher Query Test",
            Email = $"voucher-query-{Guid.NewGuid():N}@example.invalid",
            Role = role,
            IsActive = true
        };
        user.PasswordHash = hasher.HashPassword(user, "FlowerShop#2026");
        return user;
    }

    private static string GetTestConnection()
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test", StringComparison.OrdinalIgnoreCase)
            || sql.MultipleActiveResultSets)
            throw new InvalidOperationException("Use FlowerShop_Test with MARS disabled.");
        return connection;
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
