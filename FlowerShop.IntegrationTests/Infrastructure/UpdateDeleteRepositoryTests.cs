using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class UpdateDeleteRepositoryTests
{
    // Shared test setup with automatic transaction rollback
    private static async Task RunWithTransactionAsync(
        Func<IUnitOfWork, FlowerShopDbContext, Task> testAction)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Test connection string is not configured.");
        }

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

        var services = new ServiceCollection();
        services.AddInfrastructure(connectionString);

        await using var provider =
            services.BuildServiceProvider(validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        // Verify the test database is ready
        Assert.True(await context.Database.CanConnectAsync());

        var migrations =
            await context.Database.GetAppliedMigrationsAsync();

        Assert.Contains(
            migrations,
            x => x.EndsWith("_InitialCreate"));

        // Keep all test changes inside one transaction
        await using var transaction =
            await context.Database.BeginTransactionAsync();

        await testAction(unitOfWork, context);

        // No test data is committed permanently
        await transaction.RollbackAsync();
    }

    // TEST 1: UPDATE
    [Fact]
    public async Task Repository_ShouldUpdateTrackedAndDetachedEntities()
    {
        await RunWithTransactionAsync(async (unitOfWork, context) =>
        {
            var repository = unitOfWork.GetRepository<Category>();

            var category = new Category
            {
                Name = "Original Category",
                Slug = $"test-update-{Guid.NewGuid():N}"
            };

            // ARRANGE: Insert test category
            await repository.AddAsync(category);
            await unitOfWork.SaveChangesAsync();

            context.ChangeTracker.Clear();

            // CASE 1: Update a tracked entity
            var tracked = await repository.GetByIdAsync(
                category.Id,
                trackChanges: true);

            Assert.NotNull(tracked);

            tracked.Name = "Updated With Tracking";

            context.ChangeTracker.DetectChanges();

            Assert.Equal(
                EntityState.Modified,
                context.Entry(tracked).State);

            var firstSave = await unitOfWork.SaveChangesAsync();
            Assert.Equal(1, firstSave);

            // Verify SQL Server
            var firstResult = await context.Categories
                .AsNoTracking()
                .SingleAsync(x => x.Id == category.Id);

            Assert.Equal("Updated With Tracking", firstResult.Name);

            // CASE 2: Update a detached entity
            context.ChangeTracker.Clear();

            var detached = await repository.GetByIdAsync(
                category.Id);

            Assert.NotNull(detached);

            Assert.Equal(
                EntityState.Detached,
                context.Entry(detached).State);

            detached.Name = "Updated With Repository";

            repository.Update(detached);

            Assert.Equal(
                EntityState.Modified,
                context.Entry(detached).State);

            var secondSave = await unitOfWork.SaveChangesAsync();
            Assert.Equal(1, secondSave);

            // Verify persisted changes
            context.ChangeTracker.Clear();

            var finalResult = await context.Categories
                .AsNoTracking()
                .SingleAsync(x => x.Id == category.Id);

            Assert.Equal(
                "Updated With Repository",
                finalResult.Name);
        });
    }

    // TEST 2: DELETE
    [Fact]
    public async Task Repository_ShouldDeleteExistingEntity()
    {
        await RunWithTransactionAsync(async (unitOfWork, context) =>
        {
            var repository = unitOfWork.GetRepository<Category>();

            var category = new Category
            {
                Name = "Category To Delete",
                Slug = $"test-delete-{Guid.NewGuid():N}"
            };

            // ARRANGE: Insert test category
            await repository.AddAsync(category);
            await unitOfWork.SaveChangesAsync();

            context.ChangeTracker.Clear();

            // Verify category exists in SQL Server
            var existsBefore = await context.Categories
                .AsNoTracking()
                .AnyAsync(x => x.Id == category.Id);

            Assert.True(existsBefore);

            // ACT: Retrieve and delete
            var categoryToDelete =
                await repository.GetByIdAsync(category.Id);

            Assert.NotNull(categoryToDelete);

            repository.Delete(categoryToDelete);

            Assert.Equal(
                EntityState.Deleted,
                context.Entry(categoryToDelete).State);

            // Persist DELETE
            var affectedEntries =
                await unitOfWork.SaveChangesAsync();

            Assert.Equal(1, affectedEntries);

            // Deleted entity is no longer tracked
            Assert.Equal(
                EntityState.Detached,
                context.Entry(categoryToDelete).State);

            // Verify category no longer exists
            var existsAfter = await context.Categories
                .AsNoTracking()
                .AnyAsync(x => x.Id == category.Id);

            Assert.False(existsAfter);
        });
    }
}
