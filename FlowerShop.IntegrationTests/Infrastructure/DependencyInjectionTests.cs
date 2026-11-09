using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure;
using FlowerShop.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public class DependencyInjectionTests
{
    [Fact]
    public void Infrastructure_ShouldResolveScopedServices()
    {
        // Arrange
        var services = new ServiceCollection();

        // Only used to configure DI. No database connection is opened.
        var connectionString = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Test connection string is not configured.");
        }

        services.AddInfrastructure(connectionString);

        using var provider = services.BuildServiceProvider(
            validateScopes: true);

        using var scope = provider.CreateScope();

        // Act
        var context = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        var repository = scope.ServiceProvider
            .GetRequiredService<IGenericRepository<Category>>();

        // Assert
        Assert.NotNull(context);
        Assert.NotNull(unitOfWork);
        Assert.NotNull(repository);

        // Same scoped instances
        Assert.Same(
            context,
            scope.ServiceProvider
                .GetRequiredService<FlowerShopDbContext>());

        Assert.Same(
            unitOfWork,
            scope.ServiceProvider
                .GetRequiredService<IUnitOfWork>());

        // A different scope gets a different DbContext
        using var secondScope = provider.CreateScope();

        var secondContext = secondScope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        Assert.NotSame(context, secondContext);
    }
}
