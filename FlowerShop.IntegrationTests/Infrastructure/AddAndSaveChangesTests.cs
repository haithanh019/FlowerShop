using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class AddAndSaveChangesTests
{
    [Fact]
    public async Task AddAsync_ShouldPersistOnlyAfterSaveChanges()
    {
        // ARRANGE: Get test connection string
        var connectionString = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Test connection string is not configured.");
        }

        // Safety: allow only the test database
        var sqlBuilder =
            new SqlConnectionStringBuilder(connectionString);

        if (!string.Equals(
            sqlBuilder.InitialCatalog,
            "FlowerShop_Test",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Tests must use FlowerShop_Test.");
        }

        // Configure DI
        var services = new ServiceCollection();

        services.AddInfrastructure(connectionString);

        using var provider = services.BuildServiceProvider(
            validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        var repository = unitOfWork.GetRepository<Category>();

        // Ensure InitialCreate has been applied
        var migrations =
            await context.Database.GetAppliedMigrationsAsync();

        Assert.Contains(
            migrations,
            x => x.EndsWith("_InitialCreate"));

        // Rollback test data after verification
        await using var transaction =
            await context.Database.BeginTransactionAsync();

        var category = new Category
        {
            Name = "Test AddAsync",
            Slug = $"test-add-{Guid.NewGuid():N}",
            Description = "Testing AddAsync and SaveChangesAsync"
        };

        // Step 1: Entity is not tracked
        Assert.Equal(
            EntityState.Detached,
            context.Entry(category).State);

        // Step 2: Add to the Change Tracker
        await repository.AddAsync(category);

        Assert.Equal(
            EntityState.Added,
            context.Entry(category).State);

        // Create a second DbContext for database verification.
        // Both contexts share the same SQL connection and transaction.
        var options =
            new DbContextOptionsBuilder<FlowerShopDbContext>()
                .UseSqlServer(context.Database.GetDbConnection())
                .Options;

        await using (var verifyContext =
            new FlowerShopDbContext(options))
        {
            await verifyContext.Database.UseTransactionAsync(
                transaction.GetDbTransaction());

            // Step 3: Before SaveChanges, record does not exist
            var existsBeforeSave = await verifyContext.Categories
                .AsNoTracking()
                .AnyAsync(x => x.Id == category.Id);

            Assert.False(existsBeforeSave);

            // Step 4: Persist changes
            var affectedEntries =
                await unitOfWork.SaveChangesAsync();

            Assert.Equal(1, affectedEntries);

            // Entity becomes Unchanged
            Assert.Equal(
                EntityState.Unchanged,
                context.Entry(category).State);

            // Step 5: Read data from another DbContext
            var savedCategory = await verifyContext.Categories
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == category.Id);

            Assert.NotNull(savedCategory);
            Assert.Equal(category.Id, savedCategory.Id);
            Assert.Equal("Test AddAsync", savedCategory.Name);
            Assert.Equal(category.Slug, savedCategory.Slug);

            // Step 6: Saving with no changes returns 0
            var secondSave =
                await unitOfWork.SaveChangesAsync();

            Assert.Equal(0, secondSave);
        }

        // Step 7: Cleanup
        await transaction.RollbackAsync();
    }
}
