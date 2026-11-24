using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Policies;

namespace FlowerShop.Application.Common.Pricing;

// Pure calculation: no database, mutation, clock access, or usage reservation.
// Checkout must obtain fresh voucher and usage data under the shared voucher lock.
public static class VoucherPricingPolicy
{
    public static VoucherPrice Evaluate(
        Voucher voucher,
        decimal subtotal,
        decimal shippingFee,
        long actualUsedCount,
        long customerUsedCount,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("UTC_REQUIRED", nameof(nowUtc));

        if (!CheckoutPricingPolicy.IsStoredMoney(subtotal) || subtotal <= 0m ||
            !CheckoutPricingPolicy.IsStoredMoney(shippingFee))
            throw Conflict("CHECKOUT_TOTAL_OUT_OF_RANGE");

        ValidateTerms(voucher);

        // Verify persisted counters against committed non-cancelled Order history.
        if (voucher.UsedCount < 0 || actualUsedCount != voucher.UsedCount ||
            customerUsedCount < 0 || customerUsedCount > actualUsedCount ||
            (voucher.UsageLimit is { } limit && actualUsedCount > limit))
            throw Conflict("VOUCHER_USAGE_INCONSISTENT");

        var status = VoucherAvailabilityPolicy.StatusExpression(nowUtc).Compile()(voucher);
        switch (status)
        {
            case VoucherAvailabilityStatus.Inactive:
                throw Conflict("VOUCHER_INACTIVE");
            case VoucherAvailabilityStatus.Upcoming:
                throw Conflict("VOUCHER_NOT_STARTED");
            case VoucherAvailabilityStatus.Expired:
                throw Conflict("VOUCHER_EXPIRED");
            case VoucherAvailabilityStatus.Exhausted:
                throw Conflict("VOUCHER_USAGE_LIMIT_REACHED");
            case VoucherAvailabilityStatus.Available:
                break;
            default:
                throw Conflict("VOUCHER_CONFIGURATION_INVALID");
        }

        if (subtotal < voucher.MinOrderAmount)
            throw Conflict("VOUCHER_MIN_ORDER_NOT_MET");

        if (voucher.PerUserLimit is { } perUser && customerUsedCount >= perUser)
            throw Conflict("VOUCHER_PER_USER_LIMIT_REACHED");

        // Money inputs are exact decimal(18,2). A percentage is at most 100.
        var discount = voucher.DiscountType == DiscountType.Percentage
            ? subtotal * voucher.DiscountValue / 100m
            : voucher.DiscountValue;

        if (voucher.MaxDiscountAmount is { } cap)
            discount = Math.Min(discount, cap);

        discount = decimal.Round(Math.Min(discount, subtotal), 2,
            MidpointRounding.AwayFromZero);

        if (discount <= 0m)
            throw Conflict("VOUCHER_DISCOUNT_ZERO");

        var total = CheckoutPricingPolicy.CalculateTotal(subtotal, shippingFee, discount);
        return new VoucherPrice(subtotal, shippingFee, discount, total);
    }

    private static void ValidateTerms(Voucher voucher)
    {
        if (!Enum.IsDefined(voucher.DiscountType) ||
            !CheckoutPricingPolicy.IsStoredMoney(voucher.DiscountValue) ||
            voucher.DiscountValue <= 0m ||
            (voucher.DiscountType == DiscountType.Percentage && voucher.DiscountValue > 100m) ||
            !CheckoutPricingPolicy.IsStoredMoney(voucher.MinOrderAmount) ||
            (voucher.MaxDiscountAmount is { } cap &&
             (voucher.DiscountType != DiscountType.Percentage ||
              !CheckoutPricingPolicy.IsStoredMoney(cap) || cap <= 0m)) ||
            voucher.UsageLimit is <= 0 || voucher.PerUserLimit is <= 0 ||
            voucher.StartAt >= voucher.EndAt)
            throw Conflict("VOUCHER_CONFIGURATION_INVALID");
    }

    private static BusinessException Conflict(string code) =>
        new(BusinessErrorType.Conflict, code);
}
