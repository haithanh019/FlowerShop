
using FlowerShop.Application;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public class CategoryQueryServiceTests
{
    [Fact]
    public async Task CategoryQueryService_ShouldProjectFilterAndPaginate()
    {
        // 1. Connection string for test database only
        var connectionString =
            Environment.GetEnvironmentVariable(
                "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Test connection string is missing.");
        }

        var sqlBuilder =
            new SqlConnectionStringBuilder(connectionString);

        if (!string.Equals(
            sqlBuilder.InitialCatalog,
            "FlowerShop_Test",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Only FlowerShop_Test is allowed.");
        }

        // 2. Register Application and Infrastructure
        var services = new ServiceCollection();

        services.AddApplication();
        services.AddInfrastructure(connectionString);

        await using var provider =
            services.BuildServiceProvider(validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        var queryService = scope.ServiceProvider
            .GetRequiredService<ICategoryQueryService>();

        // 3. Verify the test database is migrated
        var migrations =
            await context.Database.GetAppliedMigrationsAsync();

        Assert.Contains(
            migrations,
            x => x.EndsWith("_InitialCreate"));

        // 4. Begin rollback-only transaction
        await using var transaction =
            await context.Database.BeginTransactionAsync();

        var prefix = $"test-category-odata-{Guid.NewGuid():N}";

        var repository = unitOfWork.GetRepository<Category>();

        var categoryA = new Category
        {
            Name = "Hoa A",
            Slug = $"{prefix}-a"
        };

        var categoryB = new Category
        {
            Name = "Hoa B",
            Slug = $"{prefix}-b"
        };

        var categoryC = new Category
        {
            Name = "Hoa C",
            Slug = $"{prefix}-c"
        };

        await repository.AddAsync(categoryA);
        await repository.AddAsync(categoryB);
        await repository.AddAsync(categoryC);

        Assert.Equal(3, await unitOfWork.SaveChangesAsync());

        context.ChangeTracker.Clear();

        // 5. Query projection, filtering and pagination
        var query = queryService.QueryCategories()
            .Where(x => x.Slug.StartsWith(prefix))
            .OrderBy(x => x.Name)
            .Skip(1)
            .Take(1);

        var sql = query.ToQueryString();

        Assert.Contains("WHERE", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("OFFSET", sql);

        var results = await query.ToListAsync();

        Assert.Single(results);
        Assert.Equal(categoryB.Id, results[0].Id);
        Assert.Equal("Hoa B", results[0].Name);

        // 6. Read a category by ID
        var byId = await queryService.GetByIdAsync(categoryA.Id);

        Assert.NotNull(byId);
        Assert.Equal(categoryA.Id, byId.Id);
        Assert.Equal(categoryA.Slug, byId.Slug);

        // 7. Not found case
        var notFound = await queryService.GetByIdAsync(
            Guid.NewGuid());

        Assert.Null(notFound);

        // 8. Read operations must not track entities
        Assert.Empty(context.ChangeTracker.Entries());

        // 9. Cleanup
        await transaction.RollbackAsync();
    }
}
