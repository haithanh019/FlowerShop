using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Queries;
using FlowerShop.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FlowerShop.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register Query Services
        services.AddScoped<
            ICategoryQueryService,
            CategoryQueryService>();

        services.AddScoped<ICategoryService, CategoryService>();
        return services;
    }
}
