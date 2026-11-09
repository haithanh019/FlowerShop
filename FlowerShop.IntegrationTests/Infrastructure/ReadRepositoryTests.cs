using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class ReadRepositoryTests
{
    [Fact]
    public async Task Repository_ShouldSupportGetByIdAndGetBy()
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

        // 2. Ensure only test database is used
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

        // 3. Configure DI
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

        // 6. Seed test data
        var category = new Category
        {
            Name = "Read Test Category",
            Slug = $"test-read-{Guid.NewGuid():N}",
            Description = "Testing repository read methods"
        };

        await repository.AddAsync(category);
        await unitOfWork.SaveChangesAsync();

        // Clear tracking to ensure queries hit database
        context.ChangeTracker.Clear();

        // TEST 1: GetByIdAsync - NoTracking
        var byId = await repository.GetByIdAsync(category.Id);

        Assert.NotNull(byId);
        Assert.Equal(category.Id, byId.Id);
        Assert.Equal(category.Name, byId.Name);

        Assert.Equal(
            EntityState.Detached,
            context.Entry(byId).State);

        // TEST 2: GetByIdAsync - Tracking
        var trackedById = await repository.GetByIdAsync(
            category.Id,
            trackChanges: true);

        Assert.NotNull(trackedById);

        Assert.Equal(
            EntityState.Unchanged,
            context.Entry(trackedById).State);

        // TEST 3: GetByIdAsync - Not Found
        var notFoundById = await repository.GetByIdAsync(
            Guid.NewGuid());

        Assert.Null(notFoundById);

        // Clear tracker before testing GetByAsync
        context.ChangeTracker.Clear();

        // TEST 4: GetByAsync - NoTracking
        var bySlug = await repository.GetByAsync(
            x => x.Slug == category.Slug);

        Assert.NotNull(bySlug);
        Assert.Equal(category.Id, bySlug.Id);

        Assert.Equal(
            EntityState.Detached,
            context.Entry(bySlug).State);

        // TEST 5: GetByAsync - Tracking
        var trackedBySlug = await repository.GetByAsync(
            x => x.Slug == category.Slug,
            trackChanges: true);

        Assert.NotNull(trackedBySlug);

        Assert.Equal(
            EntityState.Unchanged,
            context.Entry(trackedBySlug).State);

        // TEST 6: GetByAsync - Not Found
        var notFoundBySlug = await repository.GetByAsync(
            x => x.Slug == "non-existent-category");

        Assert.Null(notFoundBySlug);

        // 7. Cleanup test data
        await transaction.RollbackAsync();
    }
}
