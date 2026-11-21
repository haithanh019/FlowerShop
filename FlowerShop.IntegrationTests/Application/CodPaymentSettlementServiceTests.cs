using FlowerShop.Application;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using FlowerShop.Infrastructure.DependencyInjection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class CodPaymentSettlementServiceTests
{
    [Fact]
    public async Task ConfirmCollected_ShouldValidateAndPersistOnce()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing test connection.");

        var sql = new SqlConnectionStringBuilder(connection);

        if (!string.Equals(
                sql.InitialCatalog,
                "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase) ||
            sql.MultipleActiveResultSets)
        {
            throw new InvalidOperationException(
                "Use FlowerShop_Test with MARS disabled.");
        }

        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(connection);

        await using var provider = services.BuildServiceProvider(
            validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var service = scope.ServiceProvider
            .GetRequiredService<ICodPaymentSettlementService>();

        var suffix = Guid.NewGuid().ToString("N");

        var admin = new User
        {
            FullName = "COD Settlement Admin",
            Email = $"cod-admin-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login",
            Role = UserRole.Admin,
            IsActive = true
        };

        var customer = new User
        {
            FullName = "COD Settlement Customer",
            Email = $"cod-customer-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login",
            Role = UserRole.Customer,
            IsActive = true
        };

        var category = new Category
        {
            Name = "COD Settlement Category",
            Slug = $"cod-category-{suffix}"
        };

        var product = new Product
        {
            Name = "COD Test Rose",
            Slug = $"cod-product-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 10,
            IsActive = true
        };

        var order = new Order
        {
            UserId = customer.Id,
            OrderCode = $"FS-{Guid.NewGuid():N}",
            Status = OrderStatus.Pending,

            RecipientName = "COD Test Customer",
            RecipientPhone = "0900000000",
            ShippingAddressLine = "1 Flower Street",
            ShippingWard = "Test Ward",
            ShippingDistrict = "Test District",
            ShippingProvince = "Da Nang",
            ShippingCountryCode = "VN",

            Subtotal = 100000m,
            ShippingFee = 0m,
            DiscountAmount = 0m,
            TotalAmount = 100000m,

            Items =
            [
                new OrderItem
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    UnitPrice = 100000m,
                    Quantity = 1
                }
            ],

            Payments =
            [
                new Payment
                {
                    Method = PaymentMethod.CashOnDelivery,
                    Status = PaymentStatus.Pending,
                    Amount = 100000m,
                    Currency = "VND"
                }
            ]
        };

        db.AddRange(admin, customer, category, product, order);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        try
        {
            // 1. Customer cannot confirm COD collection.
            var unauthorized = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.ConfirmCollectedAsync(
                    customer.Id, order.Id));

            Assert.Equal(
                "ADMIN_ACCESS_REQUIRED",
                unauthorized.Message);

            // 2. Missing Order is rejected.
            var notFound = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.ConfirmCollectedAsync(
                    admin.Id, Guid.NewGuid()));

            Assert.Equal("ORDER_NOT_FOUND", notFound.Message);

            // 3. COD cannot be collected while Order is Pending.
            var notShipping = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.ConfirmCollectedAsync(
                    admin.Id, order.Id));

            Assert.Equal(
                "COD_PAYMENT_NOT_SETTLEABLE",
                notShipping.Message);

            // Move Order to Shipping for settlement tests.
            await db.Orders
                .Where(x => x.Id == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.Status, OrderStatus.Shipping));

            db.ChangeTracker.Clear();

            // 4. Invalid Payment amount blocks collection.
            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.Amount, 99999m));

            db.ChangeTracker.Clear();

            var invalidPayment = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.ConfirmCollectedAsync(
                    admin.Id, order.Id));

            Assert.Equal(
                "ORDER_PAYMENT_STATE_INVALID",
                invalidPayment.Message);

            // Restore Payment amount.
            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.Amount, 100000m));

            db.ChangeTracker.Clear();

            // 5. Invalid OrderItems block collection.
            await db.OrderItems
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.UnitPrice, 125000m));

            db.ChangeTracker.Clear();

            var invalidItems = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.ConfirmCollectedAsync(
                    admin.Id, order.Id));

            Assert.Equal(
                "INVALID_ORDER_ITEMS",
                invalidItems.Message);

            // Restore OrderItem price.
            await db.OrderItems
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.UnitPrice, 100000m));

            db.ChangeTracker.Clear();

            // 6. Valid COD collection succeeds.
            var result = await service.ConfirmCollectedAsync(
                admin.Id, order.Id);

            Assert.Equal(order.Id, result.OrderId);
            Assert.Equal(order.OrderCode, result.OrderCode);
            Assert.Equal(PaymentStatus.Paid, result.Status);
            Assert.Equal(100000m, result.Amount);
            Assert.Equal("VND", result.Currency);
            Assert.Equal(
                DateTimeKind.Utc,
                result.PaidAtUtc.Kind);

            var persistedPayment = await db.Payments
                .AsNoTracking()
                .SingleAsync(x => x.OrderId == order.Id);

            Assert.Equal(
                persistedPayment.Id,
                result.PaymentId);

            Assert.Equal(
                PaymentStatus.Paid,
                persistedPayment.Status);

            Assert.NotNull(persistedPayment.PaidAt);
            Assert.NotNull(persistedPayment.UpdatedAt);

            // 7. Repeated settlement must be rejected.
            var duplicate = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.ConfirmCollectedAsync(
                    admin.Id, order.Id));

            Assert.Equal(
                "COD_PAYMENT_NOT_SETTLEABLE",
                duplicate.Message);

            var afterDuplicate = await db.Payments
                .AsNoTracking()
                .SingleAsync(x => x.OrderId == order.Id);

            Assert.Equal(
                PaymentStatus.Paid,
                afterDuplicate.Status);

            Assert.Equal(
                persistedPayment.PaidAt,
                afterDuplicate.PaidAt);

            // 8. Order and Stock must remain unchanged.
            Assert.Equal(
                OrderStatus.Shipping,
                await db.Orders.AsNoTracking()
                    .Where(x => x.Id == order.Id)
                    .Select(x => x.Status)
                    .SingleAsync());

            Assert.Equal(
                10,
                await db.Products.AsNoTracking()
                    .Where(x => x.Id == product.Id)
                    .Select(x => x.StockQuantity)
                    .SingleAsync());
        }
        finally
        {
            db.ChangeTracker.Clear();

            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteDeleteAsync();

            await db.OrderItems
                .Where(x => x.OrderId == order.Id)
                .ExecuteDeleteAsync();

            await db.Orders
                .Where(x => x.Id == order.Id)
                .ExecuteDeleteAsync();

            await db.Products
                .Where(x => x.Id == product.Id)
                .ExecuteDeleteAsync();

            await db.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x =>
                    x.Id == admin.Id ||
                    x.Id == customer.Id)
                .ExecuteDeleteAsync();
        }
    }
}
