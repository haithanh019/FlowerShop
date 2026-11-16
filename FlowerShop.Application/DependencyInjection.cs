using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Queries;
using FlowerShop.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using FlowerShop.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace FlowerShop.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register Query Services
        services.AddScoped<ICategoryQueryService,CategoryQueryService>();
        services.AddScoped<IProductQueryService,ProductQueryService>();
        services.AddScoped<IProductImageQueryService, ProductImageQueryService>();
        services.AddScoped<ICartQueryService, CartQueryService>();

        // Register Application Services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPasswordHasher<User>,PasswordHasher<User>>();

        // Register Domain Services
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IProductStockService, ProductStockService>();
        services.AddScoped<IProductImageService, ProductImageService>();
        services.AddScoped<ICartService, CartService>();

        return services;
    }
}
