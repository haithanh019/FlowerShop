using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class UniqueConstraintTests
{
    [Fact]
    public async Task SaveChanges_ShouldRollback_WhenSlugIsDuplicated()
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

        // 2. Validate test database
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

        // Savepoints are not supported when MARS is enabled
        if (sqlBuilder.MultipleActiveResultSets)
        {
            throw new InvalidOperationException(
                "Disable MultipleActiveResultSets for this test.");
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

        // 5. Start transaction
        await using var transaction =
            await context.Database.BeginTransactionAsync();

        // 6. Create two categories with the same Slug
        var duplicateSlug =
            $"test-duplicate-{Guid.NewGuid():N}";

        var category1 = new Category
        {
            Name = "Test Category 1",
            Slug = duplicateSlug
        };

        var category2 = new Category
        {
            Name = "Test Category 2",
            Slug = duplicateSlug
        };

        // 7. Add both entities
        await repository.AddAsync(category1);
        await repository.AddAsync(category2);

        Assert.Equal(
            EntityState.Added,
            context.Entry(category1).State);

        Assert.Equal(
            EntityState.Added,
            context.Entry(category2).State);

        // 8. SaveChanges must fail
        var exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                async () =>
                {
                    await unitOfWork.SaveChangesAsync();
                });

        // 9. Verify SQL Server unique index violation
        var sqlException =
            Assert.IsType<SqlException>(exception.InnerException);

        Assert.Contains(
            sqlException.Number,
            new[] { 2601, 2627 });

        // 10. Clear entities after failed SaveChanges
        context.ChangeTracker.Clear();

        // 11. Verify no partial records were saved
        var savedCount = await context.Categories
            .AsNoTracking()
            .CountAsync(x => x.Slug == duplicateSlug);

        Assert.Equal(0, savedCount);

        // 12. Rollback the test transaction
        await transaction.RollbackAsync();
    }
}
