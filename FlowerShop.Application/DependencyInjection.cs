using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Queries;
using FlowerShop.Application.Services;
using FlowerShop.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddScoped<IOrderQueryService, OrderQueryService>();
        services.AddScoped<IAdminOrderQueryService, AdminOrderQueryService>();

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
        services.AddScoped<ICheckoutPricingService, CheckoutPricingService>();
        services.AddScoped<ICheckoutService, CheckoutService>();
        services.AddScoped<IAdminOrderStatusService, AdminOrderStatusService>();
        services.AddScoped<IOrderCancellationService, OrderCancellationService>();
        services.AddScoped<ICodPaymentSettlementService, CodPaymentSettlementService>();
        services.AddScoped<IUserAddressService, UserAddressService>();
        services.AddScoped<IUserProfileService, UserProfileService>();
        services.AddScoped<IAdminVoucherQueryService, AdminVoucherQueryService>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<IVoucherService, VoucherService>();
        services.AddScoped<IVoucherPreviewService, VoucherPreviewService>();
        services.AddScoped<ICustomerVoucherQueryService, CustomerVoucherQueryService>();

        return services;
    }
}
