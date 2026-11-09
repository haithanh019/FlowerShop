using Microsoft.Extensions.DependencyInjection;

namespace FlowerShop.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register Application services here.
        // Example: ICategoryService, IProductService, etc.

        return services;
    }
}
