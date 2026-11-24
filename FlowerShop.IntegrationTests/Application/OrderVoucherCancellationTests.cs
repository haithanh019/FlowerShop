using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class OrderVoucherCancellationTests
{
    [Theory]
    [InlineData(false, false, false)] // Customer, pending, active.
    [InlineData(true, true, false)]   // Admin, confirmed, inactive.
    [InlineData(false, false, true)]  // Customer, pending, expired.
    public async Task Cancel_ShouldReleaseVoucherOnceAndPreserveSnapshots(
        bool admin, bool deactivate, bool expire)
    {
        await WithDatabaseAsync(async s =>
        {
            var result = await RedeemAsync(s);
            var original = await ReadOrderAsync(s, result.OrderId);
            var originalPayment = await ReadPaymentAsync(s, result.OrderId);
            Assert.Equal(s.Voucher.Id, original.VoucherId);
            Assert.Equal(100000m, original.DiscountAmount);
            Assert.Equal(400000m, original.TotalAmount);
            Assert.Equal(1, await CountAsync(s));
            Assert.Equal(8, await StockAsync(s, s.First.Id));
            Assert.Equal(9, await StockAsync(s, s.Second.Id));

            if (admin)
            {
                using var scope = s.Factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAdminOrderStatusService>()
                    .UpdateStatusAsync(s.Admin.Id, result.OrderId, OrderStatus.Confirmed);
                await ExpectErrorAsync("INVALID_ORDER_STATUS_TRANSITION", () => CancelAsync(s, result.OrderId));
            }
            if (deactivate)
                await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                    .ExecuteUpdateAsync(p => p.SetProperty(x => x.IsActive, false));
            if (expire)
                await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                    .ExecuteUpdateAsync(p => p.SetProperty(x => x.EndAt, s.Voucher.StartAt.AddHours(1)));

            var beforeRelease = await ReadVoucherAsync(s);
            await ExpectErrorAsync("ORDER_NOT_FOUND", () => CancelAsync(s, result.OrderId, actor: s.Other.Id));
            Assert.Equal(1, await CountAsync(s));
            var cancelled = await CancelAsync(s, result.OrderId, admin);
            var order = await ReadOrderAsync(s, result.OrderId);
            var payment = await ReadPaymentAsync(s, result.OrderId);
            Assert.Equal(OrderStatus.Cancelled, order.Status);
            Assert.Equal(PaymentStatus.Cancelled, payment.Status);
            Assert.Equal(original.VoucherId, order.VoucherId);
            Assert.Equal(original.CheckoutRequestFingerprint, order.CheckoutRequestFingerprint);
            Assert.Equal(original.Subtotal, order.Subtotal);
            Assert.Equal(original.ShippingFee, order.ShippingFee);
            Assert.Equal(original.DiscountAmount, order.DiscountAmount);
            Assert.Equal(original.TotalAmount, order.TotalAmount);
            Assert.Equal(originalPayment.Amount, payment.Amount);
            Assert.Equal(originalPayment.Method, payment.Method);
            Assert.Equal(originalPayment.Currency, payment.Currency);
            Assert.Null(payment.PaidAt);
            Assert.Equal(2, await s.Db.OrderItems.CountAsync(x => x.OrderId == result.OrderId));
            Assert.Equal(10, await StockAsync(s, s.First.Id));
            Assert.Equal(10, await StockAsync(s, s.Second.Id));
            var restored = await ReadVoucherAsync(s);
            Assert.Equal(0, restored.UsedCount);
            Assert.Equal(beforeRelease.IsActive, restored.IsActive);
            Assert.Equal(beforeRelease.EndAt, restored.EndAt);
            Assert.Equal(beforeRelease.DiscountValue, restored.DiscountValue);
            Assert.NotNull(restored.UpdatedAt);
            Assert.Equal(0L, await ActualUsageAsync(s));
            Assert.False(await s.Db.CartItems.AnyAsync(x => x.CartId == s.Cart.Id));
            Assert.Equal(result.OrderId, cancelled.OrderId);
            Assert.Equal(admin ? OrderStatus.Confirmed : OrderStatus.Pending, cancelled.PreviousStatus);
            Assert.Equal(OrderStatus.Cancelled, cancelled.Status);

            await ExpectErrorAsync("ORDER_ALREADY_CANCELLED", () => CancelAsync(s, result.OrderId, !admin));
            Assert.Equal(0, await CountAsync(s));
            Assert.Equal(10, await StockAsync(s, s.First.Id));
            Assert.Equal(10, await StockAsync(s, s.Second.Id));
            Assert.Equal(restored.UpdatedAt, (await ReadVoucherAsync(s)).UpdatedAt);
        });
    }

    [Fact]
    public async Task Cancel_ShouldRejectInconsistentUsageWithoutRestoringAnything()
    {
        await WithDatabaseAsync(async s =>
        {
            var result = await RedeemAsync(s);
            // Deliberately simulate a legacy mismatch, not an allowed application write.
            await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.UsedCount, 0));
            var before = await ReadVoucherAsync(s);
            Assert.Equal(1L, await ActualUsageAsync(s));
            await ExpectErrorAsync("VOUCHER_USAGE_INCONSISTENT", () => CancelAsync(s, result.OrderId));
            Assert.Equal(0, await CountAsync(s));
            Assert.Equal(before.UpdatedAt, (await ReadVoucherAsync(s)).UpdatedAt);
            Assert.Equal(OrderStatus.Pending, (await ReadOrderAsync(s, result.OrderId)).Status);
            Assert.Equal(PaymentStatus.Pending, (await ReadPaymentAsync(s, result.OrderId)).Status);
            Assert.Equal(8, await StockAsync(s, s.First.Id));
            Assert.Equal(9, await StockAsync(s, s.Second.Id));
            Assert.False(await s.Db.CartItems.AnyAsync(x => x.CartId == s.Cart.Id));
        });
    }

    [Fact]
    public async Task Cancel_StockOverflow_ShouldRollbackVoucherAndEarlierRestock()
    {
        await WithDatabaseAsync(async s =>
        {
            var result = await RedeemAsync(s);
            // First.Id sorts before Second.Id, so the first restock succeeds before overflow.
            await s.Db.Products.Where(x => x.Id == s.Second.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.StockQuantity, int.MaxValue));
            var before = await ReadVoucherAsync(s);
            var orderBefore = await ReadOrderAsync(s, result.OrderId);
            var paymentBefore = await ReadPaymentAsync(s, result.OrderId);
            await ExpectErrorAsync("STOCK_RESTORATION_FAILED", () => CancelAsync(s, result.OrderId));
            Assert.Equal(1, await CountAsync(s));
            Assert.Equal(1L, await ActualUsageAsync(s));
            Assert.Equal(before.UpdatedAt, (await ReadVoucherAsync(s)).UpdatedAt);
            Assert.Equal(8, await StockAsync(s, s.First.Id));
            Assert.Equal(int.MaxValue, await StockAsync(s, s.Second.Id));
            var orderAfter = await ReadOrderAsync(s, result.OrderId);
            var paymentAfter = await ReadPaymentAsync(s, result.OrderId);
            Assert.Equal(OrderStatus.Pending, orderAfter.Status);
            Assert.Equal(orderBefore.UpdatedAt, orderAfter.UpdatedAt);
            Assert.Equal(PaymentStatus.Pending, paymentAfter.Status);
            Assert.Equal(paymentBefore.UpdatedAt, paymentAfter.UpdatedAt);
            Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
            Assert.Equal(paymentBefore.Amount, paymentAfter.Amount);

            // Once the stock problem is removed, the same order can be cancelled once.
            await s.Db.Products.Where(x => x.Id == s.Second.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.StockQuantity, 9));
            await CancelAsync(s, result.OrderId);
            Assert.Equal(0, await CountAsync(s));
            Assert.Equal(0L, await ActualUsageAsync(s));
            Assert.Equal(10, await StockAsync(s, s.First.Id));
            Assert.Equal(10, await StockAsync(s, s.Second.Id));
            Assert.Equal(OrderStatus.Cancelled, (await ReadOrderAsync(s, result.OrderId)).Status);
        });
    }

    [Fact]
    public async Task Cancel_ShouldRejectLinkedOrderWithoutPositiveDiscount()
    {
        await WithDatabaseAsync(async s =>
        {
            var result = await RedeemAsync(s);
            // Simulate a financially balanced but invalid historical voucher link.
            await s.Db.Orders.Where(x => x.Id == result.OrderId).ExecuteUpdateAsync(p => p
                .SetProperty(x => x.DiscountAmount, 0m).SetProperty(x => x.TotalAmount, 500000m));
            await s.Db.Payments.Where(x => x.OrderId == result.OrderId)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Amount, 500000m));
            await ExpectErrorAsync("ORDER_VOUCHER_DATA_INVALID", () => CancelAsync(s, result.OrderId));
            Assert.Equal(1, await CountAsync(s));
            Assert.Equal(1L, await ActualUsageAsync(s));
            Assert.Equal(OrderStatus.Pending, (await ReadOrderAsync(s, result.OrderId)).Status);
            Assert.Equal(PaymentStatus.Pending, (await ReadPaymentAsync(s, result.OrderId)).Status);
            Assert.Equal(8, await StockAsync(s, s.First.Id));
            Assert.Equal(9, await StockAsync(s, s.Second.Id));
        });
    }

    private static async Task<CheckoutResultDto> RedeemAsync(State s)
    {
        using var scope = s.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.False(sp.GetRequiredService<CheckoutVoucherRollout>().Enabled);
        var service = new CheckoutService(sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IProductStockRepository>(), sp.GetRequiredService<ICheckoutPricingService>(),
            sp.GetRequiredService<IVoucherUsageWriteRepository>(), new CheckoutVoucherRollout(true));
        return await service.CheckoutAsync(s.Customer.Id, new CheckoutRequestDto
        {
            CheckoutRequestId = Guid.NewGuid(), ShippingAddressId = s.Address.Id,
            PaymentMethod = PaymentMethod.CashOnDelivery, VoucherCode = s.Voucher.Code
        });
    }

    private static async Task<OrderStatusUpdateResultDto> CancelAsync(State s, Guid orderId,
        bool admin = false, Guid? actor = null)
    {
        using var scope = s.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        return admin ? await service.CancelByAdminAsync(actor ?? s.Admin.Id, orderId)
            : await service.CancelByCustomerAsync(actor ?? s.Customer.Id, orderId);
    }

    private static async Task ExpectErrorAsync(string code, Func<Task<OrderStatusUpdateResultDto>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => { await action(); });
        Assert.Equal(code, ex.Message);
        Assert.Equal(code == "ORDER_NOT_FOUND" ? BusinessErrorType.NotFound : BusinessErrorType.Conflict, ex.ErrorType);
    }

    private static Task<Order> ReadOrderAsync(State s, Guid id) =>
        s.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
    private static Task<Payment> ReadPaymentAsync(State s, Guid id) =>
        s.Db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == id);
    private static Task<Voucher> ReadVoucherAsync(State s) =>
        s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
    private static Task<int> CountAsync(State s) =>
        s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id).Select(x => x.UsedCount).SingleAsync();
    private static Task<long> ActualUsageAsync(State s) =>
        s.Db.Orders.LongCountAsync(x => x.VoucherId == s.Voucher.Id && x.Status != OrderStatus.Cancelled);
    private static Task<int> StockAsync(State s, Guid id) =>
        s.Db.Products.Where(x => x.Id == id).Select(x => x.StockQuantity).SingleAsync();

    private static async Task WithDatabaseAsync(Func<State, Task> action)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        User NewUser(UserRole role) => new()
        {
            FullName = "Voucher Cancellation Test", Email = $"vc-{Guid.NewGuid():N}@example.invalid",
            PasswordHash = "not-used-for-login", IsActive = true, Role = role
        };
        var customer = NewUser(UserRole.Customer);
        var other = NewUser(UserRole.Customer);
        var admin = NewUser(UserRole.Admin);
        var category = new Category { Name = "Voucher Cancellation", Slug = $"vc-cat-{Guid.NewGuid():N}" };
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(x => x).ToArray();
        Product NewProduct(Guid id, decimal price) => new()
        {
            Id = id, CategoryId = category.Id, Name = "Rose", Slug = $"vc-product-{id:N}",
            Price = price, StockQuantity = 10, IsActive = true
        };
        var first = NewProduct(ids[0], 150000m);
        var second = NewProduct(ids[1], 200000m);
        var cart = new Cart { UserId = customer.Id };
        var address = new UserAddress { UserId = customer.Id, RecipientName = "Customer", RecipientPhone = "0900000000",
            AddressLine = "Test Street", Ward = "Ward", District = "District", Province = "Da Nang", CountryCode = "VN", IsDefault = true };
        var now = DateTime.UtcNow;
        var voucher = new Voucher
        {
            Code = $"VC-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, MinOrderAmount = 300000m, UsageLimit = 1, PerUserLimit = 1,
            UsedCount = 0, IsActive = true, StartAt = now.AddDays(-2), EndAt = now.AddDays(2)
        };
        Guid[] userIds = [customer.Id, other.Id, admin.Id];
        try
        {
            db.AddRange(customer, other, admin, category, first, second, cart, address, voucher);
            db.CartItems.AddRange(new CartItem { CartId = cart.Id, ProductId = first.Id, Quantity = 2 },
                new CartItem { CartId = cart.Id, ProductId = second.Id, Quantity = 1 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await action(new State(factory, db, customer, other, admin, first, second, cart, address, voucher));
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orderIds = await db.Orders.Where(x => userIds.Contains(x.UserId)).Select(x => x.Id).ToArrayAsync();
            await db.Payments.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.OrderItems.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(x => orderIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.CartItems.Where(x => x.CartId == cart.Id).ExecuteDeleteAsync();
            await db.Carts.Where(x => x.Id == cart.Id).ExecuteDeleteAsync();
            await db.UserAddresses.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync();
            await db.Products.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id).ExecuteDeleteAsync();
            await db.Vouchers.Where(x => x.Id == voucher.Id).ExecuteDeleteAsync();
            await db.Users.Where(x => userIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed record State(TestApiFactory Factory, FlowerShopDbContext Db, User Customer, User Other,
        User Admin, Product First, Product Second, Cart Cart, UserAddress Address, Voucher Voucher);
}
