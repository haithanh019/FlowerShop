
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class ReadOnlyQueryRepositoryTests
{
    [Fact]
    public async Task Query_ShouldSupportFilteringSortingAndPagination()
    {
        // ARRANGE: Get test connection string
        var connectionString =
            Environment.GetEnvironmentVariable(
                "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Test connection string is not configured.");
        }

        // Safety: only allow test database
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

        // Configure Dependency Injection
        var services = new ServiceCollection();

        services.AddInfrastructure(connectionString);

        await using var provider =
            services.BuildServiceProvider(validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        var queryRepository = scope.ServiceProvider
            .GetRequiredService<IReadOnlyQueryRepository<Category>>();

        var categoryRepository =
            unitOfWork.GetRepository<Category>();

        // Verify database is ready
        Assert.True(await context.Database.CanConnectAsync());

        var migrations =
            await context.Database.GetAppliedMigrationsAsync();

        Assert.Contains(
            migrations,
            x => x.EndsWith("_InitialCreate"));

        // Rollback-only transaction
        await using var transaction =
            await context.Database.BeginTransactionAsync();

        var prefix = $"test-query-{Guid.NewGuid():N}";

        // Create test categories
        var categoryA = new Category
        {
            Name = "Test A",
            Slug = $"{prefix}-a"
        };

        var categoryB = new Category
        {
            Name = "Test B",
            Slug = $"{prefix}-b"
        };

        var categoryC = new Category
        {
            Name = "Test C",
            Slug = $"{prefix}-c"
        };

        await categoryRepository.AddAsync(categoryA);
        await categoryRepository.AddAsync(categoryB);
        await categoryRepository.AddAsync(categoryC);

        var saved = await unitOfWork.SaveChangesAsync();

        Assert.Equal(3, saved);

        // Remove tracked entities
        context.ChangeTracker.Clear();

        // ACT: Build query without executing it
        var query = queryRepository.Query()
            .Where(x => x.Slug.StartsWith(prefix))
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Skip(1)
            .Take(1);

        // Verify generated SQL
        var sql = query.ToQueryString();

        Assert.Contains("WHERE", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("OFFSET", sql);

        // Verify total filtered count
        var totalCount = await queryRepository.Query()
            .Where(x => x.Slug.StartsWith(prefix))
            .CountAsync();

        Assert.Equal(3, totalCount);

        // Execute paginated query
        var result = await query.ToListAsync();

        // ASSERT: Pagination returns the second category
        Assert.Single(result);

        Assert.Equal(categoryB.Id, result[0].Id);
        Assert.Equal("Test B", result[0].Name);

        // No entity should be tracked
        Assert.Empty(context.ChangeTracker.Entries());

        // CLEANUP
        await transaction.RollbackAsync();
    }
}
