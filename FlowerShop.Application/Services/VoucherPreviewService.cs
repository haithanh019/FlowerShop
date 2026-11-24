using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class VoucherPreviewService(
    IUnitOfWork unitOfWork,
    IVoucherUsageReadRepository usage,
    IUserAccessService userAccessService,
    TimeProvider timeProvider) : IVoucherPreviewService
{
    public async Task<VoucherPreviewResultDto> PreviewAsync(
        Guid userId,
        VoucherPreviewRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");
        ArgumentNullException.ThrowIfNull(dto);
        Validator.ValidateObject(dto, new ValidationContext(dto), validateAllProperties: true);
        var code = VoucherInputPolicy.NormalizeCode(dto.VoucherCode)!;

        if (!await userAccessService.IsActiveWithRoleAsync(
                userId, nameof(UserRole.Customer), cancellationToken))
            throw new BusinessException(BusinessErrorType.Unauthorized, "CUSTOMER_ACCESS_REQUIRED");

        // All repository reads are no-tracking. Do not open a write transaction.
        var voucher = await unitOfWork.GetRepository<Voucher>().GetByAsync(
            x => x.Code == code, cancellationToken: cancellationToken)
            ?? throw new BusinessException(BusinessErrorType.NotFound, "VOUCHER_NOT_FOUND");

        // SQL collation can match noncanonical legacy codes. Do not silently redeem them.
        if (!StringComparer.Ordinal.Equals(voucher.Code, code))
            throw Conflict("VOUCHER_CODE_NOT_CANONICAL");

        var subtotal = await GetCartSubtotalAsync(userId, cancellationToken);
        var actualUsedCount = await usage.CountNonCancelledOrdersAsync(
            voucher.Id, cancellationToken: cancellationToken);
        var customerUsedCount = await usage.CountNonCancelledOrdersAsync(
            voucher.Id, userId, cancellationToken);

        var now = VoucherInputPolicy.ToUtcMilliseconds(timeProvider.GetUtcNow());
        var price = VoucherPricingPolicy.Evaluate(voucher, subtotal,
            CheckoutPricingPolicy.CodShippingFee, actualUsedCount, customerUsedCount, now);

        // Nonbinding quote only: no usage reservation. Reads may observe concurrent changes.
        return new VoucherPreviewResultDto(voucher.Code, price.Subtotal,
            price.ShippingFee, price.DiscountAmount, price.TotalAmount, now);
    }

    private async Task<decimal> GetCartSubtotalAsync(Guid userId, CancellationToken ct)
    {
        var cart = await unitOfWork.GetRepository<Cart>().GetByAsync(
            x => x.UserId == userId, cancellationToken: ct)
            ?? throw Conflict("CART_EMPTY");

        var items = await unitOfWork.GetRepository<CartItem>().FindAsync(
            x => x.CartId == cart.Id, cancellationToken: ct);
        if (items.Count == 0)
            throw Conflict("CART_EMPTY");
        if (items.Any(x => x.Quantity <= 0))
            throw Conflict("CART_INVALID_QUANTITY");

        var ids = items.Select(x => x.ProductId).Distinct().ToArray();
        var products = await unitOfWork.GetRepository<Product>().FindAsync(
            x => ids.Contains(x.Id), cancellationToken: ct);
        var lookup = products.ToDictionary(x => x.Id);
        var lines = new List<(decimal UnitPrice, int Quantity)>(items.Count);

        // Like Checkout, reject the entire cart if ANY item is unavailable.
        foreach (var item in items)
        {
            if (!lookup.TryGetValue(item.ProductId, out var product))
                throw Conflict("CART_PRODUCT_UNAVAILABLE");
            if (!product.IsActive)
                throw Conflict("PRODUCT_INACTIVE");
            if (product.StockQuantity < item.Quantity)
                throw Conflict("INSUFFICIENT_STOCK");
            lines.Add((product.Price, item.Quantity));
        }

        return CheckoutPricingPolicy.CalculateSubtotal(lines);
    }

    private static BusinessException Conflict(string code) =>
        new(BusinessErrorType.Conflict, code);
}
