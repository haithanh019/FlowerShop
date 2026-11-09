using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class SharedDbContextTests
{
    [Fact]
    public async Task Repositories_ShouldShareSameDbContext()
    {
        // Arrange: Get test connection string
        var connectionString = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Test connection string is not configured.");
        }

        // Safety: only allow the test database
        var builder = new SqlConnectionStringBuilder(connectionString);

        if (!string.Equals(
            builder.InitialCatalog,
            "FlowerShop_Test",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Tests must use FlowerShop_Test database.");
        }

        // Configure Dependency Injection
        var services = new ServiceCollection();

        services.AddInfrastructure(connectionString);

        using var provider = services.BuildServiceProvider(
            validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        // Resolve UnitOfWork and DbContext
        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        var context = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        // Verify test database and migration
        Assert.True(await context.Database.CanConnectAsync());

        var migrations = await context.Database
            .GetAppliedMigrationsAsync();

        Assert.Contains(
            migrations,
            x => x.EndsWith("_InitialCreate"));

        // Get repositories from the same UnitOfWork
        var categoryRepo = unitOfWork.GetRepository<Category>();
        var productRepo = unitOfWork.GetRepository<Product>();

        // Use a transaction to prevent permanent test data
        await using var transaction =
            await context.Database.BeginTransactionAsync();

        var suffix = Guid.NewGuid().ToString("N");

        var category = new Category
        {
            Name = "Integration Test Category",
            Slug = $"it-shared-category-{suffix}"
        };

        var product = new Product
        {
            Name = "Integration Test Flower",
            Slug = $"it-shared-product-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 10,
            IsActive = true
        };

        // Act: Add entities through different repositories
        await categoryRepo.AddAsync(category);
        await productRepo.AddAsync(product);

        // Assert: Both entities share the same Change Tracker
        Assert.Equal(
            EntityState.Added,
            context.Entry(category).State);

        Assert.Equal(
            EntityState.Added,
            context.Entry(product).State);

        // Save all changes through UnitOfWork
        var result = await unitOfWork.SaveChangesAsync();

        Assert.Equal(2, result);

        // Verify records exist in SQL Server
        var savedCategory = await context.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == category.Id);

        var savedProduct = await context.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == product.Id);

        Assert.NotNull(savedCategory);
        Assert.NotNull(savedProduct);

        Assert.Equal(category.Id, savedProduct.CategoryId);

        // Cleanup: Rollback the test transaction
        await transaction.RollbackAsync();
    }
}
