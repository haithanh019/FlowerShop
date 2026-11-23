
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.Infrastructure.DependencyInjection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public sealed class AdminOrderReadRepositoryTests
{
    [Fact]
    public async Task AdminOrderRead_ShouldFilterPaginateAndLoadDetails()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

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
        services.AddInfrastructure(connection);

        await using var provider = services.BuildServiceProvider(
            validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var repository = scope.ServiceProvider
            .GetRequiredService<IAdminOrderReadRepository>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        User NewCustomer(string label) => new()
        {
            FullName = label,
            Email = $"admin-query-{label}-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login",
            Role = UserRole.Customer,
            IsActive = true
        };

        var alice = NewCustomer("Alice");
        var bob = NewCustomer("Bob");

        var category = new Category
        {
            Name = "Admin Order Query Category",
            Slug = $"admin-order-query-{suffix}"
        };

        var product = new Product
        {
            Name = "Test Rose",
            Slug = $"admin-order-rose-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 10,
            IsActive = true
        };

        Order NewOrder(
            User customer,
            string label,
            OrderStatus status,
            DateTime createdAt)
        {
            return new Order
            {
                UserId = customer.Id,
                OrderCode = $"FS-{label}-{suffix[..12]}",
                Status = status,
                CreatedAt = createdAt,

                RecipientName = customer.FullName,
                RecipientPhone = "0900000000",
                ShippingAddressLine = "123 Test Street",
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
                        ProductName = "Rose Snapshot",
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
        }

        var oldest = NewOrder(
            alice, "A-OLD", OrderStatus.Pending,
            now.AddDays(-4));

        var middle = NewOrder(
            alice, "A-MID", OrderStatus.Confirmed,
            now.AddDays(-3));

        var bobShipping = NewOrder(
            bob, "B-NEW", OrderStatus.Shipping,
            now.AddDays(-2));

        var aliceLatest = NewOrder(
            alice, "A-LATE", OrderStatus.Pending,
            now.AddDays(-1));

        var bobLatest = NewOrder(
            bob, "B-TIE", OrderStatus.Pending,
            now.AddDays(-1));

        var seededOrders = new[]
        {
            oldest,
            middle,
            bobShipping,
            aliceLatest,
            bobLatest
        };

        db.AddRange(
            alice, bob, category, product);

        db.Orders.AddRange(seededOrders);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        try
        {
            // 1. All orders must be visible to Admin Repository.
            var (Orders, TotalCount) = await repository.GetPageAsync(
                null, null, null, 1, 2);

            Assert.Equal(5, TotalCount);
            Assert.Equal(2, Orders.Count);

            // CreatedAt DESC and Id DESC break timestamp ties.
            var expectedOrder = seededOrders
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Select(x => x.Id)
                .ToArray();

            var secondPage = await repository.GetPageAsync(
                null, null, null, 2, 2);

            var thirdPage = await repository.GetPageAsync(
                null, null, null, 3, 2);

            Assert.Equal(
                [.. expectedOrder.Take(2)],
                [.. Orders.Select(x => x.Id)]);

            Assert.Equal(
                [.. expectedOrder.Skip(2).Take(2)],
                [.. secondPage.Orders.Select(x => x.Id)]);

            Assert.Equal(
                [.. expectedOrder.Skip(4).Take(2)],
                [.. thirdPage.Orders.Select(x => x.Id)]);

            // 2. Status filter.
            var pending = await repository.GetPageAsync(
                OrderStatus.Pending, null, null, 1, 10);

            Assert.Equal(3, pending.TotalCount);
            Assert.All(
                pending.Orders,
                x => Assert.Equal(OrderStatus.Pending, x.Status));

            // 3. Customer filter.
            var aliceOrders = await repository.GetPageAsync(
                null, alice.Id, null, 1, 10);

            Assert.Equal(3, aliceOrders.TotalCount);
            Assert.All(
                aliceOrders.Orders,
                x => Assert.Equal(alice.Id, x.UserId));

            // 4. Search by OrderCode keyword.
            var searched = await repository.GetPageAsync(
                null, null, " A-MID ", 1, 10);

            Assert.Equal(1, searched.TotalCount);
            Assert.Equal(
                middle.Id,
                Assert.Single(searched.Orders).Id);

            // 5. Combined filters.
            var combined = await repository.GetPageAsync(
                OrderStatus.Pending,
                alice.Id,
                "A-LATE",
                1,
                10);

            Assert.Equal(1, combined.TotalCount);
            Assert.Equal(
                aliceLatest.Id,
                Assert.Single(combined.Orders).Id);

            // 6. Empty search result.
            var empty = await repository.GetPageAsync(
                null, null, "NO-SUCH-ORDER", 1, 10);

            Assert.Empty(empty.Orders);
            Assert.Equal(0, empty.TotalCount);

            // 7. Detail must include User, Items and Payments.
            var detail = await repository.GetByIdAsync(
                aliceLatest.Id);

            Assert.NotNull(detail);
            Assert.Equal(alice.Id, detail.UserId);
            Assert.Equal(alice.Email, detail.User.Email);

            Assert.Equal(
                product.Id,
                Assert.Single(detail.Items).ProductId);

            Assert.Equal(
                PaymentStatus.Pending,
                Assert.Single(detail.Payments).Status);

            // 8. Repository queries are read-only.
            Assert.Equal(
                EntityState.Detached,
                db.Entry(detail).State);

            Assert.Empty(db.ChangeTracker.Entries<Order>());

            // 9. Missing Order returns null.
            var missing = await repository.GetByIdAsync(
                Guid.NewGuid());

            Assert.Null(missing);

            // 10. Invalid pagination must be rejected.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => repository.GetPageAsync(
                    null, null, null, 0, 10));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => repository.GetPageAsync(
                    null, null, null, 1, 51));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => repository.GetPageAsync(
                    (OrderStatus)999, null, null, 1, 10));

            await Assert.ThrowsAsync<ArgumentException>(
                () => repository.GetPageAsync(
                    null, Guid.Empty, null, 1, 10));

            await Assert.ThrowsAsync<ArgumentException>(
                () => repository.GetByIdAsync(Guid.Empty));
        }
        finally
        {
            // Clean up only data created by this test.
            db.ChangeTracker.Clear();

            var orderIds = seededOrders
                .Select(x => x.Id)
                .ToArray();

            await db.Payments
                .Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();

            await db.OrderItems
                .Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();

            await db.Orders
                .Where(x => orderIds.Contains(x.Id))
                .ExecuteDeleteAsync();

            await db.Products
                .Where(x => x.Id == product.Id)
                .ExecuteDeleteAsync();

            await db.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x => x.Id == alice.Id ||
                            x.Id == bob.Id)
                .ExecuteDeleteAsync();
        }
    }
}
