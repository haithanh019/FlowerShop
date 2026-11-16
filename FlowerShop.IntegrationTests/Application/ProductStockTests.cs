using FlowerShop.Application;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Infrastructure.Data;
using FlowerShop.Infrastructure.DependencyInjection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public class ProductStockTests
{
    [Fact]
    public async Task ConcurrentDecrease_ShouldPreventOverselling()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing test connection.");

        var sql = new SqlConnectionStringBuilder(connection);

        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase)
            || sql.MultipleActiveResultSets)
            throw new InvalidOperationException("Invalid test database.");

        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(connection);

        await using var provider = services.BuildServiceProvider();
        await using var seedScope = provider.CreateAsyncScope();

        var db = seedScope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var category = new Category
        {
            Name = "Stock Test",
            Slug = $"test-stock-category-{Guid.NewGuid():N}"
        };

        var product = new Product
        {
            Name = "Stock Test Product",
            Slug = $"test-stock-product-{Guid.NewGuid():N}",
            Price = 100000,
            StockQuantity = 5,
            IsActive = true,
            CategoryId = category.Id
        };

        db.AddRange(category, product);
        await db.SaveChangesAsync();

        try
        {
            await using var scopeA = provider.CreateAsyncScope();
            await using var scopeB = provider.CreateAsyncScope();

            var serviceA = scopeA.ServiceProvider
                .GetRequiredService<IProductStockService>();

            var serviceB = scopeB.ServiceProvider
                .GetRequiredService<IProductStockService>();

            async Task<bool> TryDecrease(IProductStockService service)
            {
                try
                {
                    await service.DecreaseAsync(product.Id, 4);
                    return true;
                }
                catch (BusinessException ex)
                    when (ex.Message == "INSUFFICIENT_STOCK")
                {
                    return false;
                }
            }

            var results = await Task.WhenAll(
                TryDecrease(serviceA),
                TryDecrease(serviceB));

            Assert.Single(results, x => x);

            var remaining = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            Assert.Equal(1, remaining);

            // Increasing stock should also work
            await serviceA.IncreaseAsync(product.Id, 2);

            var updatedStock = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            Assert.Equal(3, updatedStock);
        }
        finally
        {
            db.Products.Remove(product);
            db.Categories.Remove(category);
            await db.SaveChangesAsync();
        }
    }
}
