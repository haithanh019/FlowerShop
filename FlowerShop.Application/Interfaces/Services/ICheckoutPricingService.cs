using FlowerShop.Application.Common.Pricing;

namespace FlowerShop.Application.Interfaces.Services;

public interface ICheckoutPricingService
{
    // For voucher pricing, caller must own the cart transaction. Continue on the
    // same scoped UnitOfWork until redemption and order commit (future step D).
    Task<CheckoutPrice> PrepareAsync(
        Guid userId, string? voucherCode, decimal subtotal,
        CancellationToken cancellationToken = default);
}
