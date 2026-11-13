using System.ComponentModel.DataAnnotations;
using FlowerShop.Application;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public class CategoryServiceTests
{
    private static string NewSlug()
        => $"test-category-{Guid.NewGuid():N}";

    [Fact]
    public async Task CreateUpdateDelete_ShouldPersistChanges()
    {
        await RunAsync(async (service, uow) =>
        {
            var slug = NewSlug();

            var created = await service.CreateAsync(new()
            {
                Name = "  Hoa hong  ",
                Slug = slug
            });

            Assert.Equal("Hoa hong", created.Name);

            var updated = await service.UpdateAsync(created.Id, new()
            {
                Name = "Hoa hong do",
                Slug = slug,
                Description = "  Updated  "
            });

            Assert.Equal("Updated", updated.Description);
            Assert.NotNull(updated.UpdatedAt);

            var repo = uow.GetRepository<Category>();
            var stored = await repo.GetByIdAsync(created.Id);

            Assert.Equal("Hoa hong do", stored?.Name);

            await service.DeleteAsync(created.Id);

            Assert.Null(await repo.GetByIdAsync(created.Id));
        });
    }

    [Fact]
    public async Task Create_ShouldRejectDuplicateSlug()
    {
        await RunAsync(async (service, _) =>
        {
            var slug = NewSlug();

            await service.CreateAsync(new()
            {
                Name = "Hoa A",
                Slug = slug
            });

            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.CreateAsync(new()
                {
                    Name = "Hoa B",
                    Slug = slug
                }));

            Assert.Equal(BusinessErrorType.Conflict, ex.ErrorType);
            Assert.Equal("CATEGORY_SLUG_EXISTS", ex.Message);
        });
    }

    [Fact]
    public async Task Update_ShouldRejectAnotherCategorySlug()
    {
        await RunAsync(async (service, _) =>
        {
            var firstSlug = NewSlug();
            var secondSlug = NewSlug();

            var first = await service.CreateAsync(new()
            {
                Name = "Hoa A",
                Slug = firstSlug
            });

            await service.CreateAsync(new()
            {
                Name = "Hoa B",
                Slug = secondSlug
            });

            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.UpdateAsync(first.Id, new()
                {
                    Name = "Hoa A updated",
                    Slug = secondSlug
                }));

            Assert.Equal(BusinessErrorType.Conflict, ex.ErrorType);
            Assert.Equal("CATEGORY_SLUG_EXISTS", ex.Message);
        });
    }

    [Fact]
    public async Task Delete_ShouldRejectCategoryWithProducts()
    {
        await RunAsync(async (service, uow) =>
        {
            var category = await service.CreateAsync(new()
            {
                Name = "Hoa hong",
                Slug = NewSlug()
            });

            await uow.GetRepository<Product>().AddAsync(new Product
            {
                Name = "Bo hoa hong",
                Slug = NewSlug(),
                Price = 100000,
                StockQuantity = 5,
                CategoryId = category.Id
            });

            await uow.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.DeleteAsync(category.Id));

            Assert.Equal(BusinessErrorType.Conflict, ex.ErrorType);
            Assert.Equal("CATEGORY_HAS_PRODUCTS", ex.Message);
        });
    }

    [Fact]
    public async Task UpdateDelete_ShouldRejectMissingCategory()
    {
        await RunAsync(async (service, _) =>
        {
            var id = Guid.NewGuid();

            var update = await Assert.ThrowsAsync<BusinessException>(
                () => service.UpdateAsync(id, new()
                {
                    Name = "Hoa hong",
                    Slug = NewSlug()
                }));

            var delete = await Assert.ThrowsAsync<BusinessException>(
                () => service.DeleteAsync(id));

            Assert.Equal(BusinessErrorType.NotFound, update.ErrorType);
            Assert.Equal(BusinessErrorType.NotFound, delete.ErrorType);
        });
    }

    [Fact]
    public async Task Create_ShouldRejectInvalidDto()
    {
        await RunAsync(async (service, _) =>
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => service.CreateAsync(new()
                {
                    Name = "",
                    Slug = NewSlug()
                }));
        });
    }

    private static async Task RunAsync(
        Func<ICategoryService, IUnitOfWork, Task> test)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Test connection string is missing.");

        var sql = new SqlConnectionStringBuilder(connectionString);

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
        services.AddInfrastructure(connectionString);

        await using var provider =
            services.BuildServiceProvider(validateScopes: true);

        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var migrations =
            await db.Database.GetAppliedMigrationsAsync();

        Assert.Contains(
            migrations,
            x => x.EndsWith("_InitialCreate"));

        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var service = scope.ServiceProvider
            .GetRequiredService<ICategoryService>();

        var uow = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        await test(service, uow);

        await transaction.RollbackAsync();
    }
}
