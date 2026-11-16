
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

public class CheckoutConcurrencyTests
{
    [Fact]
    public async Task ConcurrentCheckout_ShouldNotDuplicateOrders()
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

        await using var setupScope =
            provider.CreateAsyncScope();

        var db = setupScope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        var suffix = Guid.NewGuid().ToString("N");

        var user = new User
        {
            FullName = "Checkout Concurrency Test",
            Email = $"checkout-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login"
        };

        var category = new Category
        {
            Name = "Checkout Test Category",
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

        var address = new UserAddress
        {
            UserId = user.Id,
            RecipientName = "Test Customer",
            RecipientPhone = "0900000000",
            AddressLine = "1 Test Street",
            Ward = "Test Ward",
            District = "Test District",
            Province = "Da Nang",
            CountryCode = "VN"
        };

        var cart = new Cart { UserId = user.Id };

        var cartItem = new CartItem
        {
            CartId = cart.Id,
            ProductId = product.Id,
            Quantity = 2
        };

        db.AddRange(
            user, category, product,
            address, cart, cartItem);

        await db.SaveChangesAsync();

        async Task<CheckoutResultDto> RunCheckoutAsync(
            Guid requestId)
        {
            // Each request must use a separate DbContext.
            await using var requestScope =
                provider.CreateAsyncScope();

            var service = requestScope.ServiceProvider
                .GetRequiredService<ICheckoutService>();

            return await service.CheckoutAsync(
                user.Id,
                new CheckoutRequestDto
                {
                    CheckoutRequestId = requestId,
                    ShippingAddressId = address.Id,
                    PaymentMethod =
                        PaymentMethod.CashOnDelivery
                });
        }

        try
        {
            // SCENARIO 1:
            // Two requests use the same CheckoutRequestId.
            var requestId = Guid.NewGuid();

            var firstTask = RunCheckoutAsync(requestId);
            var secondTask = RunCheckoutAsync(requestId);

            var results = await Task.WhenAll(
                firstTask, secondTask);

            Assert.Equal(
                results[0].OrderId,
                results[1].OrderId);

            Assert.Equal(
                results[0].OrderCode,
                results[1].OrderCode);

            Assert.Equal(1, await db.Orders
                .AsNoTracking()
                .CountAsync(x => x.UserId == user.Id));

            Assert.Equal(1, await db.Payments
                .AsNoTracking()
                .CountAsync(x =>
                    x.OrderId == results[0].OrderId));

            var stock = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            Assert.Equal(8, stock);

            Assert.False(await db.CartItems
                .AsNoTracking()
                .AnyAsync(x => x.CartId == cart.Id));

            // SCENARIO 2:
            // Create another CartItem for this user.
            db.CartItems.Add(new CartItem
            {
                CartId = cart.Id,
                ProductId = product.Id,
                Quantity = 1
            });

            await db.SaveChangesAsync();

            // Different RequestIds compete for the same Cart.
            async Task<(
                CheckoutResultDto? Result,
                Exception? Error)> AttemptAsync(Guid id)
            {
                try
                {
                    return (await RunCheckoutAsync(id), null);
                }
                catch (Exception ex)
                {
                    return (null, ex);
                }
            }

            var attempts = await Task.WhenAll(
                AttemptAsync(Guid.NewGuid()),
                AttemptAsync(Guid.NewGuid()));

            // Only one should create an Order.
            Assert.Single(attempts, x => x.Result is not null);

            // The other should find an empty Cart.
            Assert.Single(attempts, x => x.Error is BusinessException
            {
                ErrorType: BusinessErrorType.Conflict
            });

            // Stock: 10 - 2 - 1 = 7.
            stock = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            Assert.Equal(7, stock);

            Assert.Equal(2, await db.Orders
                .AsNoTracking()
                .CountAsync(x => x.UserId == user.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();

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

            await db.CartItems
                .Where(x => x.CartId == cart.Id)
                .ExecuteDeleteAsync();

            await db.Carts
                .Where(x => x.Id == cart.Id)
                .ExecuteDeleteAsync();

            await db.UserAddresses
                .Where(x => x.Id == address.Id)
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
