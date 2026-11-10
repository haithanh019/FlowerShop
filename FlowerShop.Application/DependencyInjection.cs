using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Queries;
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

        return services;
    }
}
