using FlowerShop.Application;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Orders;
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

public sealed class OrderCancellationServiceTests
{
    [Fact]
    public async Task Cancel_ShouldEnforceOwnershipRestoreOnceAndRollbackOnFailure()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase) || sql.MultipleActiveResultSets)
        {
            throw new InvalidOperationException(
                "Use FlowerShop_Test with MARS disabled.");
        }

        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(connection);

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var setupScope = provider.CreateAsyncScope();
        var db = setupScope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(),
            m => m.EndsWith("_InitialCreate"));

        var suffix = Guid.NewGuid().ToString("N");
        User NewUser(string name, UserRole role) => new()
        {
            FullName = name,
            Email = $"cancel-{name.ToLowerInvariant()}-{suffix}@example.invalid",
            PasswordHash = "test-not-for-login",
            Role = role,
            IsActive = true
        };

        var alice = NewUser("Alice", UserRole.Customer);
        var bob = NewUser("Bob", UserRole.Customer);
        var admin = NewUser("Admin", UserRole.Admin);
        var category = new Category
        {
            Name = "Cancellation Test Category",
            Slug = $"cancel-category-{suffix}"
        };

        // Ensure successful restock happens BEFORE the deliberate overflow
        // in the rollback scenario, regardless of randomized GUIDs.
        var productIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(x => x).ToArray();

        var rose = new Product
        {
            Id = productIds[0],
            Name = "Test Rose",
            Slug = $"cancel-rose-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 5,
            IsActive = true
        };
        var tulip = new Product
        {
            Id = productIds[1],
            Name = "Test Tulip",
            Slug = $"cancel-tulip-{suffix}",
            CategoryId = category.Id,
            Price = 80000m,
            StockQuantity = 0,
            IsActive = true
        };
        var overflowProduct = new Product
        {
            Id = productIds[2],
            Name = "Overflow Stock Product",
            Slug = $"cancel-overflow-{suffix}",
            CategoryId = category.Id,
            Price = 10000m,
            StockQuantity = int.MaxValue,
            IsActive = true
        };

        Order NewOrder(
            User owner,
            OrderStatus status,
            PaymentStatus paymentStatus,
            params (Product Product, int Quantity)[] lines)
        {
            var items = lines.Select(line => new OrderItem
            {
                ProductId = line.Product.Id,
                ProductName = line.Product.Name,
                UnitPrice = line.Product.Price,
                Quantity = line.Quantity
            }).ToList();
            var amount = items.Sum(x => x.UnitPrice * x.Quantity);

            return new Order
            {
                UserId = owner.Id,
                OrderCode = $"FS-{Guid.NewGuid():N}",
                Status = status,
                RecipientName = "Test Recipient",
                RecipientPhone = "0900000000",
                ShippingAddressLine = "1 Flower Street",
                ShippingWard = "Test Ward",
                ShippingDistrict = "Test District",
                ShippingProvince = "Da Nang",
                ShippingCountryCode = "VN",
                Subtotal = amount,
                ShippingFee = 0m,
                DiscountAmount = 0m,
                TotalAmount = amount,
                Items = items,
                Payments =
                [
                    new Payment
                    {
                        Method = PaymentMethod.CashOnDelivery,
                        Status = paymentStatus,
                        Amount = amount,
                        Currency = "VND"
                    }
                ]
            };
        }

        var pending = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 2), (tulip, 1));
        var confirmed = NewOrder(alice, OrderStatus.Confirmed,
            PaymentStatus.Pending, (rose, 1));
        var paid = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Paid, (rose, 1));
        var rollback = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 1), (overflowProduct, 1));
        var bobPending = NewOrder(bob, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 1));
        var orderIds = new[]
        {
            pending.Id, confirmed.Id, paid.Id, rollback.Id, bobPending.Id
        };

        db.AddRange(alice, bob, admin, category,
            rose, tulip, overflowProduct,
            pending, confirmed, paid, rollback, bobPending);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Give every application call its own DbContext/transaction.
        async Task<OrderStatusUpdateResultDto> RunAsync(
            Func<IOrderCancellationService,
                Task<OrderStatusUpdateResultDto>> action)
        {
            await using var requestScope = provider.CreateAsyncScope();
            var service = requestScope.ServiceProvider
                .GetRequiredService<IOrderCancellationService>();
            return await action(service);
        }

        Task<OrderStatusUpdateResultDto> CancelCustomer(Guid userId, Guid orderId)
            => RunAsync(s => s.CancelByCustomerAsync(userId, orderId));

        Task<OrderStatusUpdateResultDto> CancelAdmin(Guid userId, Guid orderId)
            => RunAsync(s => s.CancelByAdminAsync(userId, orderId));

        async Task<int> Stock(Guid id) => await db.Products.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.StockQuantity).SingleAsync();

        async Task<OrderStatus> Status(Guid id) => await db.Orders.AsNoTracking()
            .Where(x => x.Id == id).Select(x => x.Status).SingleAsync();

        async Task<PaymentStatus> PaymentStatusOf(Guid orderId) =>
            await db.Payments.AsNoTracking()
                .Where(x => x.OrderId == orderId)
                .Select(x => x.Status).SingleAsync();

        try
        {
            // 1. Customer must own the Order.
            var hidden = await Assert.ThrowsAsync<BusinessException>(
                () => CancelCustomer(bob.Id, pending.Id));
            Assert.Equal("ORDER_NOT_FOUND", hidden.Message);

            // 2. No role escalation through the Admin service.
            var denied = await Assert.ThrowsAsync<BusinessException>(
                () => CancelAdmin(alice.Id, confirmed.Id));
            Assert.Equal("ORDER_CANCEL_ACCESS_REQUIRED", denied.Message);

            // 3. Only Admin can cancel a Confirmed Order.
            var confirmedDenied = await Assert.ThrowsAsync<BusinessException>(
                () => CancelCustomer(alice.Id, confirmed.Id));
            Assert.Equal("INVALID_ORDER_STATUS_TRANSITION", confirmedDenied.Message);

            // 4. Paid COD must not be cancelled automatically.
            var paidDenied = await Assert.ThrowsAsync<BusinessException>(
                () => CancelAdmin(admin.Id, paid.Id));
            Assert.Equal("PAYMENT_NOT_CANCELLABLE", paidDenied.Message);
            Assert.Equal(OrderStatus.Pending, await Status(paid.Id));

            // 5. Customer cancellation restores each product exactly once.
            var result = await CancelCustomer(alice.Id, pending.Id);
            Assert.Equal(OrderStatus.Pending, result.PreviousStatus);
            Assert.Equal(OrderStatus.Cancelled, result.Status);
            Assert.Equal(7, await Stock(rose.Id));
            Assert.Equal(1, await Stock(tulip.Id));
            Assert.Equal(OrderStatus.Cancelled, await Status(pending.Id));
            Assert.Equal(PaymentStatus.Cancelled,
                await PaymentStatusOf(pending.Id));

            var repeated = await Assert.ThrowsAsync<BusinessException>(
                () => CancelCustomer(alice.Id, pending.Id));
            Assert.Equal("ORDER_ALREADY_CANCELLED", repeated.Message);
            Assert.Equal(7, await Stock(rose.Id));
            Assert.Equal(1, await Stock(tulip.Id));

            // 6. Admin can cancel Confirmed; still no double restock.
            var adminResult = await CancelAdmin(admin.Id, confirmed.Id);
            Assert.Equal(OrderStatus.Confirmed, adminResult.PreviousStatus);
            Assert.Equal(OrderStatus.Cancelled, await Status(confirmed.Id));
            Assert.Equal(PaymentStatus.Cancelled,
                await PaymentStatusOf(confirmed.Id));
            Assert.Equal(8, await Stock(rose.Id));

            // 7. Product stock overflow in the second item must roll back
            // the already performed increase of the first item.
            var failedRestore = await Assert.ThrowsAsync<BusinessException>(
                () => CancelCustomer(alice.Id, rollback.Id));
            Assert.Equal("STOCK_RESTORATION_FAILED", failedRestore.Message);
            Assert.Equal(8, await Stock(rose.Id));
            Assert.Equal(int.MaxValue, await Stock(overflowProduct.Id));
            Assert.Equal(OrderStatus.Pending, await Status(rollback.Id));
            Assert.Equal(PaymentStatus.Pending,
                await PaymentStatusOf(rollback.Id));

            // 8. A simultaneous Admin/Customer cancellation is serialized
            // by the same OrderId lock. Only one can restore stock.
            async Task<(OrderStatusUpdateResultDto? Result, Exception? Error)>
                AttemptAsync(Func<Task<OrderStatusUpdateResultDto>> operation)
            {
                try { return (await operation(), null); }
                catch (Exception ex) { return (null, ex); }
            }

            var attempts = await Task.WhenAll(
                AttemptAsync(() => CancelCustomer(bob.Id, bobPending.Id)),
                AttemptAsync(() => CancelAdmin(admin.Id, bobPending.Id)));

            Assert.Single(attempts.Where(x => x.Result is not null));
            var error = Assert.Single(attempts.Where(x => x.Error is not null)).Error;
            var conflict = Assert.IsType<BusinessException>(error);
            Assert.Equal("ORDER_ALREADY_CANCELLED", conflict.Message);
            Assert.Equal(9, await Stock(rose.Id));
            Assert.Equal(OrderStatus.Cancelled, await Status(bobPending.Id));
            Assert.Equal(PaymentStatus.Cancelled,
                await PaymentStatusOf(bobPending.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Payments.Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();
            await db.OrderItems.Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();
            await db.Orders.Where(x => orderIds.Contains(x.Id))
                .ExecuteDeleteAsync();
            await db.Products.Where(x =>
                x.Id == rose.Id || x.Id == tulip.Id || x.Id == overflowProduct.Id)
                .ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();
            await db.Users.Where(x =>
                x.Id == alice.Id || x.Id == bob.Id || x.Id == admin.Id)
                .ExecuteDeleteAsync();
        }
    }
}
