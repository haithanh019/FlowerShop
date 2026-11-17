using System.ComponentModel.DataAnnotations;
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

public sealed class AdminOrderStatusServiceTests
{
    [Fact]
    public async Task UpdateStatus_ShouldFollowPolicyAndPersistChanges()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing test connection.");

        var sql = new SqlConnectionStringBuilder(connection);

        if (!string.Equals(
                sql.InitialCatalog,
                "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase)
            || sql.MultipleActiveResultSets)
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
            .GetRequiredService<IAdminOrderStatusService>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        var suffix = Guid.NewGuid().ToString("N");

        var admin = new User
        {
            FullName = "Order Status Admin Test",
            Email = $"status-admin-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login",
            Role = UserRole.Admin,
            IsActive = true
        };

        var customer = new User
        {
            FullName = "Order Status Customer Test",
            Email = $"status-customer-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login",
            Role = UserRole.Customer,
            IsActive = true
        };

        var category = new Category
        {
            Name = "Status Test Category",
            Slug = $"status-category-{suffix}"
        };

        var product = new Product
        {
            Name = "Status Test Rose",
            Slug = $"status-product-{suffix}",
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

            RecipientName = "Test Customer",
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

        try
        {
            // Customer must not update Order status.
            var unauthorized = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    customer.Id,
                    order.Id,
                    OrderStatus.Confirmed));

            Assert.Equal(
                "ADMIN_ACCESS_REQUIRED",
                unauthorized.Message);

            // Admin cannot skip states.
            var invalid = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id,
                    order.Id,
                    OrderStatus.Shipping));

            Assert.Equal(
                "INVALID_ORDER_STATUS_TRANSITION",
                invalid.Message);

            // Cancellation must use its own workflow.
            var cancel = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id,
                    order.Id,
                    OrderStatus.Cancelled));

            Assert.Equal(
                "USE_ORDER_CANCELLATION",
                cancel.Message);

            // Invalid enum value -> validation error.
            await Assert.ThrowsAsync<ValidationException>(() =>
                service.UpdateStatusAsync(
                    admin.Id,
                    order.Id,
                    (OrderStatus)999));

            // Missing Order -> NotFound.
            var notFound = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id,
                    Guid.NewGuid(),
                    OrderStatus.Confirmed));

            Assert.Equal("ORDER_NOT_FOUND", notFound.Message);

            // Valid progression:


            // Invalid payment states must block order progression.
            var invalidPaymentStatuses = new[]
            {
                PaymentStatus.Failed,
                PaymentStatus.Cancelled,
                PaymentStatus.Refunded
            };

            foreach (var invalidPaymentStatus in invalidPaymentStatuses)
            {
                await db.Payments
                    .Where(x => x.OrderId == order.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(
                            x => x.Status,
                            invalidPaymentStatus));

                var paymentError =
                    await Assert.ThrowsAsync<BusinessException>(
                        () => service.UpdateStatusAsync(
                            admin.Id,
                            order.Id,
                            OrderStatus.Confirmed));

                Assert.Equal(
                    "ORDER_PAYMENT_STATE_INVALID",
                    paymentError.Message);

                var currentOrderStatus = await db.Orders
                    .AsNoTracking()
                    .Where(x => x.Id == order.Id)
                    .Select(x => x.Status)
                    .SingleAsync();

                Assert.Equal(
                    OrderStatus.Pending,
                    currentOrderStatus);
            }

            // Restore a valid COD payment state
            // for the existing progression tests.
            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        x => x.Status,
                        PaymentStatus.Pending));

            // Pending -> Confirmed -> Preparing
            // -> Shipping -> Delivered
            var expectedStatuses = new[]
            {
                OrderStatus.Confirmed,
                OrderStatus.Preparing,
                OrderStatus.Shipping,
                OrderStatus.Delivered
            };

            var previous = OrderStatus.Pending;

            foreach (var next in expectedStatuses)
            {
                var result = await service.UpdateStatusAsync(
                    admin.Id,
                    order.Id,
                    next);

                Assert.Equal(order.Id, result.OrderId);
                Assert.Equal(order.OrderCode, result.OrderCode);
                Assert.Equal(previous, result.PreviousStatus);
                Assert.Equal(next, result.Status);
                Assert.Equal(
                    DateTimeKind.Utc,
                    result.UpdatedAtUtc.Kind);

                var persistedStatus = await db.Orders
                    .AsNoTracking()
                    .Where(x => x.Id == order.Id)
                    .Select(x => x.Status)
                    .SingleAsync();

                Assert.Equal(next, persistedStatus);

                previous = next;
            }

            // Delivered is terminal.
            await Assert.ThrowsAsync<BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id,
                    order.Id,
                    OrderStatus.Confirmed));

            // No accidental stock or payment modifications.
            var stock = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            Assert.Equal(10, stock);

            var paymentStatus = await db.Payments
                .AsNoTracking()
                .Where(x => x.OrderId == order.Id)
                .Select(x => x.Status)
                .SingleAsync();

            Assert.Equal(
                PaymentStatus.Pending,
                paymentStatus);

            Assert.Equal(
                OrderStatus.Delivered,
                await db.Orders
                    .AsNoTracking()
                    .Where(x => x.Id == order.Id)
                    .Select(x => x.Status)
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
