using FlowerShop.Application.Common.Concurrency;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public sealed class VoucherUsageDecrementTests
{
    private static readonly DateTime OriginalUpdatedAt = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Decrement_ShouldValidateArgumentsTransactionAndCancellation()
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var usage = scope.ServiceProvider.GetRequiredService<IVoucherUsageWriteRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var id = Guid.NewGuid();
        var invalidId = await Assert.ThrowsAsync<ArgumentException>(() => usage.TryDecrementAsync(Guid.Empty, 1));
        Assert.Equal("voucherId", invalidId.ParamName);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => usage.TryDecrementAsync(id, -1));
        var noTransaction = await Assert.ThrowsAsync<InvalidOperationException>(() => usage.TryDecrementAsync(id, 1));
        Assert.Equal("TRANSACTION_REQUIRED", noTransaction.Message);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await uow.ExecuteInLockedTransactionAsync(VoucherLockKeys.ForId(id), async ct =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => usage.TryDecrementAsync(id, 1, cancelled.Token));
            return await usage.TryDecrementAsync(id, 1, ct);
        });
        Assert.False(result); // Missing row.
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(true, false)]  // Active, in period, exhausted.
    [InlineData(false, false)] // Inactive, in period.
    [InlineData(true, true)]   // Expired.
    public async Task Decrement_ShouldReleaseUseRegardlessOfAvailability(bool active, bool expired)
    {
        await WithFixtureAsync(active, expired, async s =>
        {
            using var scope = s.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var usage = scope.ServiceProvider.GetRequiredService<IVoucherUsageWriteRepository>();
            var read = scope.ServiceProvider.GetRequiredService<IVoucherUsageReadRepository>();

            var updated = await uow.ExecuteInLockedTransactionAsync(VoucherLockKeys.ForId(s.Voucher.Id), async ct =>
            {
                Assert.Equal(1L, await read.CountNonCancelledOrdersAsync(s.Voucher.Id, cancellationToken: ct));
                Assert.True(await usage.TryDecrementAsync(s.Voucher.Id, 1, ct));
                Assert.Equal(0, await db.Vouchers.Where(x => x.Id == s.Voucher.Id).Select(x => x.UsedCount).SingleAsync(ct));
                Assert.False(await usage.TryDecrementAsync(s.Voucher.Id, 0, ct)); // No underflow.
                Assert.False(await usage.TryDecrementAsync(s.Voucher.Id, 1, ct)); // Stale expectation.

                // Synthetic history fixture only: pair the counter change with its status
                // transition so the committed fixture remains consistent. This does not
                // call OrderCancellationService or test stock/payment restoration.
                var order = await db.Orders.SingleAsync(x => x.Id == s.Order.Id, ct);
                order.Status = OrderStatus.Cancelled;
                return true;
            });
            Assert.True(updated);
            using var observer = s.Factory.Services.CreateScope();
            var fresh = observer.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
            var voucher = await fresh.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
            Assert.Equal(0, voucher.UsedCount);
            Assert.NotNull(voucher.UpdatedAt);
            Assert.True(voucher.UpdatedAt > OriginalUpdatedAt);
            Assert.Equal(active, voucher.IsActive);
            Assert.Equal(s.Voucher.StartAt, voucher.StartAt);
            Assert.Equal(s.Voucher.EndAt, voucher.EndAt);
            Assert.Equal(s.Voucher.DiscountValue, voucher.DiscountValue);
            Assert.Equal(s.Voucher.UsageLimit, voucher.UsageLimit);
            var orderAfter = await fresh.Orders.AsNoTracking().SingleAsync(x => x.Id == s.Order.Id);
            Assert.Equal(OrderStatus.Cancelled, orderAfter.Status);
            Assert.Equal(s.Voucher.Id, orderAfter.VoucherId);
            Assert.Equal(80m, orderAfter.TotalAmount);
            Assert.Equal(0L, await observer.ServiceProvider.GetRequiredService<IVoucherUsageReadRepository>()
                .CountNonCancelledOrdersAsync(s.Voucher.Id));
        });
    }

    [Fact]
    public async Task Decrement_StaleExpectedCount_ShouldNotChangeCounterOrTimestamp()
    {
        await WithFixtureAsync(true, false, async s =>
        {
            using var scope = s.Factory.Services.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var usage = scope.ServiceProvider.GetRequiredService<IVoucherUsageWriteRepository>();
            var changed = await uow.ExecuteInLockedTransactionAsync(VoucherLockKeys.ForId(s.Voucher.Id), async ct =>
            {
                Assert.False(await usage.TryDecrementAsync(s.Voucher.Id, 0, ct));
                return await usage.TryDecrementAsync(s.Voucher.Id, 2, ct);
            });
            Assert.False(changed);
            await AssertOriginalAsync(s);
        });
    }

    [Fact]
    public async Task Decrement_FailureAfterSave_ShouldRollbackCounterTimestampAndOrder()
    {
        await WithFixtureAsync(false, true, async s =>
        {
            using (var scope = s.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var usage = scope.ServiceProvider.GetRequiredService<IVoucherUsageWriteRepository>();
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    uow.ExecuteInLockedTransactionAsync<bool>(VoucherLockKeys.ForId(s.Voucher.Id), async ct =>
                    {
                        Assert.True(await usage.TryDecrementAsync(s.Voucher.Id, 1, ct));
                        var order = await db.Orders.SingleAsync(x => x.Id == s.Order.Id, ct);
                        order.Status = OrderStatus.Cancelled;
                        await uow.SaveChangesAsync(ct);
                        var voucher = await db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id, ct);
                        Assert.Equal(0, voucher.UsedCount);
                        Assert.NotNull(voucher.UpdatedAt);
                        Assert.True(voucher.UpdatedAt > OriginalUpdatedAt);
                        Assert.Equal(OrderStatus.Cancelled, await db.Orders.Where(x => x.Id == s.Order.Id)
                            .Select(x => x.Status).SingleAsync(ct));
                        throw new InvalidOperationException("FORCED_DECREMENT_ROLLBACK");
                    }));
                Assert.Equal("FORCED_DECREMENT_ROLLBACK", failure.Message);
                Assert.Null(db.Database.CurrentTransaction);
            }
            await AssertOriginalAsync(s);
        });
    }

    private static async Task AssertOriginalAsync(Fixture s)
    {
        using var observer = s.Factory.Services.CreateScope();
        var db = observer.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var voucher = await db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
        Assert.Equal(1, voucher.UsedCount);
        Assert.Equal(OriginalUpdatedAt, voucher.UpdatedAt);
        Assert.Equal(OrderStatus.Pending, await db.Orders.Where(x => x.Id == s.Order.Id).Select(x => x.Status).SingleAsync());
        Assert.Equal(1L, await observer.ServiceProvider.GetRequiredService<IVoucherUsageReadRepository>()
            .CountNonCancelledOrdersAsync(s.Voucher.Id));
    }

    private static async Task WithFixtureAsync(bool active, bool expired, Func<Fixture, Task> action)
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var user = new User
        {
            FullName = "Voucher Usage Decrement Test", Email = $"vud-{Guid.NewGuid():N}@example.invalid",
            PasswordHash = "not-used-for-login", Role = UserRole.Customer, IsActive = true
        };
        var voucher = new Voucher
        {
            Code = $"VUD-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, UsageLimit = 1, PerUserLimit = 1, UsedCount = 1,
            StartAt = now.AddDays(-2), EndAt = now.AddDays(expired ? -1 : 1),
            IsActive = active, UpdatedAt = OriginalUpdatedAt
        };
        // Synthetic Order header, sufficient for count/transaction repository tests.
        // Real OrderItems and Payment restoration are tested in later steps.
        var order = new Order
        {
            UserId = user.Id, VoucherId = voucher.Id, OrderCode = $"VU-{Guid.NewGuid():N}",
            Status = OrderStatus.Pending, RecipientName = "Test Customer", RecipientPhone = "0900000000",
            ShippingAddressLine = "Test Street", ShippingWard = "Ward", ShippingDistrict = "District",
            ShippingProvince = "Da Nang", ShippingCountryCode = "VN",
            Subtotal = 100m, DiscountAmount = 20m, TotalAmount = 80m
        };
        try
        {
            db.AddRange(user, voucher, order);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await action(new Fixture(factory, voucher, order));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Orders.Where(x => x.Id == order.Id).ExecuteDeleteAsync();
            await db.Vouchers.Where(x => x.Id == voucher.Id).ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == user.Id).ExecuteDeleteAsync();
        }
    }

    private static string Connection() => Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

    private sealed record Fixture(TestApiFactory Factory, Voucher Voucher, Order Order);
}
