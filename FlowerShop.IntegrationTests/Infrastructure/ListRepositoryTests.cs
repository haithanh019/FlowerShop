using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class ListRepositoryTests
{
    [Fact]
    public async Task Repository_ShouldSupportGetAllAndFind()
    {
        // 1. Get test connection string
        var connectionString =
            Environment.GetEnvironmentVariable(
                "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Test connection string is not configured.");
        }

        // 2. Ensure only the test database is used
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

        // 3. Configure Dependency Injection
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

        // 4. Verify database migration
        var migrations =
            await context.Database.GetAppliedMigrationsAsync();

        Assert.Contains(
            migrations,
            x => x.EndsWith("_InitialCreate"));

        // 5. Start rollback-only transaction
        await using var transaction =
            await context.Database.BeginTransactionAsync();

        // Unique prefix prevents collisions with other tests
        var suffix = Guid.NewGuid().ToString("N");
        var prefix = $"test-list-{suffix}";

        // 6. Create test categories
        var category1 = new Category
        {
            Name = "Test Rose",
            Slug = $"{prefix}-rose"
        };

        var category2 = new Category
        {
            Name = "Test Tulip",
            Slug = $"{prefix}-tulip"
        };

        var category3 = new Category
        {
            Name = "Test Lily",
            Slug = $"test-other-{suffix}"
        };

        // 7. Save test data
        await repository.AddAsync(category1);
        await repository.AddAsync(category2);
        await repository.AddAsync(category3);

        var savedCount = await unitOfWork.SaveChangesAsync();

        Assert.Equal(3, savedCount);

        // Remove existing tracked entities
        context.ChangeTracker.Clear();

        // TEST 1: GetAllAsync - NoTracking
        var allCategories = await repository.GetAllAsync();

        var databaseCount = await context.Categories
            .AsNoTracking()
            .CountAsync();

        Assert.Equal(databaseCount, allCategories.Count);

        Assert.Contains(
            allCategories,
            x => x.Id == category1.Id);

        Assert.Contains(
            allCategories,
            x => x.Id == category2.Id);

        Assert.Contains(
            allCategories,
            x => x.Id == category3.Id);

        var categoryFromAll = allCategories
            .Single(x => x.Id == category1.Id);

        Assert.Equal(
            EntityState.Detached,
            context.Entry(categoryFromAll).State);

        // TEST 2: FindAsync - Filter by Slug prefix
        var filtered = await repository.FindAsync(
            x => x.Slug.StartsWith(prefix));

        Assert.Equal(2, filtered.Count);

        Assert.Contains(
            filtered,
            x => x.Id == category1.Id);

        Assert.Contains(
            filtered,
            x => x.Id == category2.Id);

        Assert.DoesNotContain(
            filtered,
            x => x.Id == category3.Id);

        // TEST 3: FindAsync - No results
        var emptyResult = await repository.FindAsync(
            x => x.Slug == $"missing-{suffix}");

        Assert.Empty(emptyResult);

        // TEST 4: FindAsync - NoTracking
        var filteredCategory = filtered[0];

        Assert.Equal(
            EntityState.Detached,
            context.Entry(filteredCategory).State);

        // TEST 5: FindAsync - Tracking
        var trackedCategories = await repository.FindAsync(
            x => x.Slug.StartsWith(prefix),
            trackChanges: true);

        Assert.Equal(2, trackedCategories.Count);

        Assert.All(trackedCategories, category =>
        {
            Assert.Equal(
                EntityState.Unchanged,
                context.Entry(category).State);
        });

        // TEST 6: GetAllAsync - Tracking
        context.ChangeTracker.Clear();

        var trackedAll = await repository.GetAllAsync(
            trackChanges: true);

        var trackedCategory = trackedAll
            .Single(x => x.Id == category3.Id);

        Assert.Equal(
            EntityState.Unchanged,
            context.Entry(trackedCategory).State);

        // 8. Cleanup
        await transaction.RollbackAsync();
    }
}
