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

        var codService = scope.ServiceProvider
            .GetRequiredService<ICodPaymentSettlementService>();

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


            // Verify consistency between PaymentStatus and PaidAt
            // before allowing early Order progression.
            var invalidSnapshots =
                new (PaymentStatus Status, DateTime? PaidAt)[]
                {
                    (PaymentStatus.Pending, DateTime.UtcNow),
                    (PaymentStatus.Paid, null),
                    (PaymentStatus.Paid, DateTime.UtcNow)
                };

            foreach (var (Status, PaidAt) in invalidSnapshots)
            {
                await db.Payments
                    .Where(x => x.OrderId == order.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(
                            x => x.Status,
                            Status)
                        .SetProperty(
                            x => x.PaidAt,
                            PaidAt));

                var inconsistent = await Assert.ThrowsAsync<
                    BusinessException>(() =>
                    service.UpdateStatusAsync(
                        admin.Id,
                        order.Id,
                        OrderStatus.Confirmed));

                Assert.Equal(
                    "ORDER_PAYMENT_STATE_INVALID",
                    inconsistent.Message);

                Assert.Equal(
                    OrderStatus.Pending,
                    await db.Orders.AsNoTracking()
                        .Where(x => x.Id == order.Id)
                        .Select(x => x.Status)
                        .SingleAsync());
            }

            // Restore a valid unpaid COD snapshot.
            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(
                        x => x.Status,
                        PaymentStatus.Pending)
                    .SetProperty(
                        x => x.PaidAt,
                        (DateTime?)null));

            db.ChangeTracker.Clear();

            // A COD payment amount different from Order.TotalAmount
            // must block order progression.
            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.Amount, 99999m));

            var invalidAmount = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id, order.Id, OrderStatus.Confirmed));

            Assert.Equal(
                "ORDER_PAYMENT_STATE_INVALID",
                invalidAmount.Message);

            // A wrong currency must also block progression.
            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Amount, 100000m)
                    .SetProperty(x => x.Currency, "USD"));

            var invalidCurrency = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id, order.Id, OrderStatus.Confirmed));

            Assert.Equal(
                "ORDER_PAYMENT_STATE_INVALID",
                invalidCurrency.Message);

            Assert.Equal(
                OrderStatus.Pending,
                await db.Orders.AsNoTracking()
                    .Where(x => x.Id == order.Id)
                    .Select(x => x.Status)
                    .SingleAsync());

            // Restore valid payment data before testing OrderItems.
            await db.Payments
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Currency, "VND"));

            // Reject OrderItems whose total differs from Subtotal.
            await db.OrderItems
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.UnitPrice, 125000m));

            var invalidItems = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id,
                    order.Id,
                    OrderStatus.Confirmed));

            Assert.Equal(
                "INVALID_ORDER_ITEMS",
                invalidItems.Message);

            Assert.Equal(
                OrderStatus.Pending,
                await db.Orders.AsNoTracking()
                    .Where(x => x.Id == order.Id)
                    .Select(x => x.Status)
                    .SingleAsync());

            // Restore the correct OrderItem amount.
            await db.OrderItems
                .Where(x => x.OrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.UnitPrice, 100000m));

            // Reject an Order that has no OrderItems.
            await db.OrderItems
                .Where(x => x.OrderId == order.Id)
                .ExecuteDeleteAsync();

            var missingItems = await Assert.ThrowsAsync<
                BusinessException>(() =>
                service.UpdateStatusAsync(
                    admin.Id,
                    order.Id,
                    OrderStatus.Confirmed));

            Assert.Equal(
                "INVALID_ORDER_ITEMS",
                missingItems.Message);

            // Clear stale EF tracking after ExecuteDelete.
            db.ChangeTracker.Clear();

            // Restore the valid OrderItem for progression tests.
            db.OrderItems.Add(new OrderItem
            {
                OrderId = order.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = 100000m,
                Quantity = 1
            });

            await db.SaveChangesAsync();

            Assert.Equal(
                OrderStatus.Pending,
                await db.Orders.AsNoTracking()
                    .Where(x => x.Id == order.Id)
                    .Select(x => x.Status)
                    .SingleAsync());

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

                if (next == OrderStatus.Delivered)
                {
                    // Shipping + Pending cannot become Delivered.
                    var unpaid = await Assert.ThrowsAsync<
                        BusinessException>(() =>
                        service.UpdateStatusAsync(
                            admin.Id,
                            order.Id,
                            OrderStatus.Delivered));

                    Assert.Equal(
                        "COD_PAYMENT_NOT_SETTLED",
                        unpaid.Message);

                    Assert.Equal(
                        OrderStatus.Shipping,
                        await db.Orders.AsNoTracking()
                            .Where(x => x.Id == order.Id)
                            .Select(x => x.Status)
                            .SingleAsync());

                    // A Paid status without PaidAt is invalid.
                    await db.Payments
                        .Where(x => x.OrderId == order.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(
                            x => x.Status,
                            PaymentStatus.Paid));

                    var missingPaidAt = await Assert.ThrowsAsync<
                        BusinessException>(() =>
                        service.UpdateStatusAsync(
                            admin.Id,
                            order.Id,
                            OrderStatus.Delivered));

                    Assert.Equal(
                        "COD_PAYMENT_NOT_SETTLED",
                        missingPaidAt.Message);

                    // Restore Pending before legitimate settlement.
                    await db.Payments
                        .Where(x => x.OrderId == order.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(
                            x => x.Status,
                            PaymentStatus.Pending));

                    db.ChangeTracker.Clear();

                    // Confirm that COD has actually been collected.
                    var settlement =
                        await codService.ConfirmCollectedAsync(
                            admin.Id,
                            order.Id);

                    Assert.Equal(
                        PaymentStatus.Paid,
                        settlement.Status);

                    Assert.Equal(
                        DateTimeKind.Utc,
                        settlement.PaidAtUtc.Kind);

                    Assert.Equal(
                        PaymentStatus.Paid,
                        await db.Payments.AsNoTracking()
                            .Where(x => x.OrderId == order.Id)
                            .Select(x => x.Status)
                            .SingleAsync());
                }



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
                PaymentStatus.Paid,
                paymentStatus);

            var paidAt = await db.Payments
                .AsNoTracking()
                .Where(x => x.OrderId == order.Id)
                .Select(x => x.PaidAt)
                .SingleAsync();

            Assert.NotNull(paidAt);


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
