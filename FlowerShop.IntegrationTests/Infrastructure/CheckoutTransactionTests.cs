using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.Infrastructure.DependencyInjection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class CheckoutTransactionTests
{
    [Fact]
    public async Task Transaction_ShouldCommitOrRollbackAllChanges()
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
        services.AddInfrastructure(connection);

        await using var provider = services.BuildServiceProvider(
            validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        var stockRepository = scope.ServiceProvider
            .GetRequiredService<IProductStockRepository>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        var suffix = Guid.NewGuid().ToString("N");

        var user = new User
        {
            FullName = "Transaction Test",
            Email = $"checkout-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login"
        };

        var category = new Category
        {
            Name = "Transaction Test Category",
            Slug = $"checkout-category-{suffix}"
        };

        var product = new Product
        {
            Name = "Test Rose",
            Slug = $"checkout-product-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 10,
            IsActive = true
        };

        db.AddRange(user, category, product);
        await db.SaveChangesAsync();

        Order NewOrder(string code, int quantity)
        {
            var total = product.Price * quantity;

            return new Order
            {
                UserId = user.Id,
                OrderCode = code,
                Status = OrderStatus.Pending,

                RecipientName = "Test Customer",
                RecipientPhone = "0900000000",
                ShippingAddressLine = "1 Test Street",
                ShippingWard = "Test Ward",
                ShippingDistrict = "Test District",
                ShippingProvince = "Da Nang",
                ShippingCountryCode = "VN",

                Subtotal = total,
                ShippingFee = 0m,
                DiscountAmount = 0m,
                TotalAmount = total,

                Items =
                [
                    new OrderItem
                    {
                        ProductId = product.Id,
                        ProductName = product.Name,
                        UnitPrice = product.Price,
                        Quantity = quantity
                    }
                ],

                Payments =
                [
                    new Payment
                    {
                        Method = PaymentMethod.CashOnDelivery,
                        Status = PaymentStatus.Pending,
                        Amount = total,
                        Currency = "VND"
                    }
                ]
            };
        }

        var successCode = $"FS-{Guid.NewGuid():N}";
        var failureCode = $"FS-{Guid.NewGuid():N}";

        try
        {
            // TEST 1: Successful transaction
            var committedOrderId =
                await unitOfWork.ExecuteInTransactionAsync(
                    async ct =>
                    {
                        var decreased =
                            await stockRepository.TryDecreaseAsync(
                                product.Id, 2, ct);

                        Assert.True(decreased);

                        var order = NewOrder(successCode, 2);

                        await unitOfWork.GetRepository<Order>()
                            .AddAsync(order, ct);

                        return order.Id;
                    });

            Assert.True(await db.Orders
                .AsNoTracking()
                .AnyAsync(x => x.Id == committedOrderId));

            Assert.Equal(1, await db.OrderItems
                .AsNoTracking()
                .CountAsync(x => x.OrderId == committedOrderId));

            Assert.Equal(1, await db.Payments
                .AsNoTracking()
                .CountAsync(x => x.OrderId == committedOrderId));

            var stockAfterCommit = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            Assert.Equal(8, stockAfterCommit);

            // TEST 2: Failure must roll back all changes
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () =>
                {
                    await unitOfWork.ExecuteInTransactionAsync<Guid>(
                        async ct =>
                        {
                            var decreased =
                                await stockRepository.TryDecreaseAsync(
                                    product.Id, 3, ct);

                            Assert.True(decreased);

                            var order = NewOrder(failureCode, 3);

                            await unitOfWork.GetRepository<Order>()
                                .AddAsync(order, ct);

                            // Force SQL INSERTs before the failure
                            await unitOfWork.SaveChangesAsync(ct);

                            throw new InvalidOperationException(
                                "FORCED_ROLLBACK");
                        });
                });

            // Discard stale tracked entities after rollback
            db.ChangeTracker.Clear();

            var stockAfterRollback = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            // Stock must remain at 8, not 5
            Assert.Equal(8, stockAfterRollback);

            // Failed Order must not exist
            Assert.False(await db.Orders
                .AsNoTracking()
                .AnyAsync(x => x.OrderCode == failureCode));

            // Previously committed Order still exists
            Assert.True(await db.Orders
                .AsNoTracking()
                .AnyAsync(x => x.OrderCode == successCode));
        }
        finally
        {
            db.ChangeTracker.Clear();

            // Delete only records created by this test
            var orderIds = await db.Orders
                .AsNoTracking()
                .Where(x => x.UserId == user.Id)
                .Select(x => x.Id)
                .ToArrayAsync();

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
                .Where(x => x.Id == user.Id)
                .ExecuteDeleteAsync();
        }
    }
}
