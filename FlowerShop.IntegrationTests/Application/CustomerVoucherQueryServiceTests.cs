using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Queries;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class CustomerVoucherQueryServiceTests
{
    private static readonly DateTime Now = new(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Available_ShouldFilterBeforePaginationAndRespectCustomerUsage()
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = new CustomerVoucherQueryService(
            scope.ServiceProvider.GetRequiredService<ICustomerVoucherReadRepository>(),
            scope.ServiceProvider.GetRequiredService<IUserAccessService>(), new FixedClock());
        var alice = NewUser(UserRole.Customer);
        var bob = NewUser(UserRole.Customer);
        var tag = $"CV-{Guid.NewGuid():N}".ToUpperInvariant();
        Voucher New(string suffix) => new()
        {
            Code = tag + suffix, DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, StartAt = Now, EndAt = Now.AddDays(1),
            IsActive = true, UsageLimit = 5, PerUserLimit = 1
        };
        var first = New("-A");
        var second = New("-B");
        second.EndAt = Now.AddDays(2);
        var used = New("-USED"); used.UsedCount = 1;
        var exhausted = New("-FULL"); exhausted.UsageLimit = 1; exhausted.UsedCount = 1;
        var expired = New("-EXP"); expired.StartAt = Now.AddDays(-1); expired.EndAt = Now;
        var upcoming = New("-FUT"); upcoming.StartAt = Now.AddMilliseconds(1);
        var inactive = New("-OFF"); inactive.IsActive = false;
        var inconsistent = New("-BAD"); inconsistent.UsedCount = 1;
        var nonCanonical = New("-lower");
        var wrongTerms = New("-FIX"); wrongTerms.DiscountType = DiscountType.FixedAmount;
        wrongTerms.MaxDiscountAmount = 10m;

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Users.AddRange(alice, bob);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var baselineAlice = (await service.GetAvailableAsync(alice.Id)).TotalCount;
            var baselineBob = (await service.GetAvailableAsync(bob.Id)).TotalCount;
            db.Vouchers.AddRange(first, second, used, exhausted, expired, upcoming,
                inactive, inconsistent, nonCanonical, wrongTerms);
            db.Orders.AddRange(NewOrder(alice.Id, used.Id, OrderStatus.Pending),
                NewOrder(alice.Id, exhausted.Id, OrderStatus.Pending),
                NewOrder(alice.Id, first.Id, OrderStatus.Cancelled));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            async Task<List<CustomerVoucherDto>> ReadAll(Guid id, int expectedCount)
            {
                const int size = 2;
                var page = await service.GetAvailableAsync(id, pageSize: size);
                Assert.Equal(expectedCount, page.TotalCount);
                Assert.Equal(Now, page.EvaluatedAtUtc);
                Assert.Equal(DateTimeKind.Utc, page.EvaluatedAtUtc.Kind);
                Assert.Equal(1 + (expectedCount - 1) / size, page.TotalPages);
                var result = page.Items.ToList();
                for (var i = 2; i <= page.TotalPages; i++)
                    result.AddRange((await service.GetAvailableAsync(id, i, size)).Items);
                Assert.Equal(expectedCount, result.Count);
                Assert.Equal(expectedCount, result.Select(x => x.Code).Distinct().Count());
                var empty = await service.GetAvailableAsync(id, page.TotalPages + 1, size);
                Assert.Empty(empty.Items);
                Assert.Equal(expectedCount, empty.TotalCount);
                return result;
            }

            var aliceRows = (await ReadAll(alice.Id, baselineAlice + 2))
                .Where(x => x.Code.StartsWith(tag, StringComparison.Ordinal)).ToArray();
            Assert.Equal(new[] { first.Code, second.Code }, aliceRows.Select(x => x.Code).ToArray());
            Assert.All(aliceRows, x => Assert.Equal(DateTimeKind.Utc, x.EndAtUtc.Kind));
            var bobRows = (await ReadAll(bob.Id, baselineBob + 3))
                .Where(x => x.Code.StartsWith(tag, StringComparison.Ordinal)).ToArray();
            Assert.Equal(new[] { first.Code, used.Code, second.Code }, bobRows.Select(x => x.Code).ToArray());
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await tx.RollbackAsync(); }
    }

    [Fact]
    public async Task Available_ShouldRejectUnauthorizedUsersAndInvalidPages()
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerVoucherQueryService>();
        var customer = NewUser(UserRole.Customer);
        var admin = NewUser(UserRole.Admin);
        var disabled = NewUser(UserRole.Customer); disabled.IsActive = false;
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Users.AddRange(customer, admin, disabled);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            foreach (var id in new[] { admin.Id, disabled.Id, Guid.NewGuid() })
            {
                var ex = await Assert.ThrowsAsync<BusinessException>(() => service.GetAvailableAsync(id));
                Assert.Equal(BusinessErrorType.Unauthorized, ex.ErrorType);
                Assert.Equal("CUSTOMER_ACCESS_REQUIRED", ex.Message);
            }
            Assert.Equal("INVALID_USER_ID", (await Assert.ThrowsAsync<ValidationException>(
                () => service.GetAvailableAsync(Guid.Empty))).Message);
            Assert.Equal("PAGE_MUST_BE_POSITIVE", (await Assert.ThrowsAsync<ValidationException>(
                () => service.GetAvailableAsync(customer.Id, page: 0))).Message);
            Assert.Equal("PAGE_SIZE_OUT_OF_RANGE", (await Assert.ThrowsAsync<ValidationException>(
                () => service.GetAvailableAsync(customer.Id, pageSize: 51))).Message);
            Assert.Equal("PAGE_OUT_OF_RANGE", (await Assert.ThrowsAsync<ValidationException>(
                () => service.GetAvailableAsync(customer.Id, int.MaxValue, 50))).Message);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await tx.RollbackAsync(); }
    }

    private static User NewUser(UserRole role) => new()
    {
        FullName = "Customer Voucher Query", Email = $"cvq-{Guid.NewGuid():N}@example.invalid",
        PasswordHash = "not-used-for-login", Role = role, IsActive = true
    };

    private static Order NewOrder(Guid userId, Guid voucherId, OrderStatus status) => new()
    {
        UserId = userId, VoucherId = voucherId, Status = status, OrderCode = $"CV-{Guid.NewGuid():N}",
        RecipientName = "Test Customer", RecipientPhone = "0901234567", ShippingAddressLine = "Test Street",
        ShippingWard = "Ward", ShippingDistrict = "District", ShippingProvince = "Da Nang",
        ShippingCountryCode = "VN", Subtotal = 100m, DiscountAmount = 20m, TotalAmount = 80m
    };

    private static string Connection() => Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
