using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Validation;

namespace FlowerShop.Application.Common.Pricing;

public static class CheckoutPricingPolicy
{
    // Match the existing COD checkout policy. Future shipping policies may replace this.
    public const decimal CodShippingFee = 0m;

    public static decimal CalculateSubtotal(
        IEnumerable<(decimal UnitPrice, int Quantity)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        decimal subtotal = 0m;
        var hasLines = false;
        try
        {
            foreach (var (price, quantity) in lines)
            {
                hasLines = true;
                if (quantity <= 0)
                    throw Conflict("CART_INVALID_QUANTITY");
                if (!IsStoredMoney(price) || price <= 0m)
                    throw Conflict("CHECKOUT_TOTAL_OUT_OF_RANGE");

                subtotal = checked(subtotal + checked(price * quantity));
                if (subtotal > VoucherInputPolicy.MaxStoredAmount)
                    throw Conflict("CHECKOUT_TOTAL_OUT_OF_RANGE");
            }
        }
        catch (OverflowException)
        {
            throw Conflict("CHECKOUT_TOTAL_OUT_OF_RANGE");
        }

        if (!hasLines)
            throw Conflict("CART_EMPTY");
        return subtotal;
    }

    public static decimal CalculateTotal(decimal subtotal, decimal shippingFee, decimal discount)
    {
        if (!IsStoredMoney(subtotal) || subtotal <= 0m ||
            !IsStoredMoney(shippingFee) || !IsStoredMoney(discount) || discount > subtotal)
            throw Conflict("CHECKOUT_TOTAL_OUT_OF_RANGE");

        // Inputs are bounded by decimal(18,2), so this arithmetic cannot overflow decimal.
        var total = subtotal + shippingFee - discount;
        if (total <= 0m || total > VoucherInputPolicy.MaxStoredAmount)
            throw Conflict("CHECKOUT_TOTAL_OUT_OF_RANGE");
        return total;
    }

    public static bool IsStoredMoney(decimal value) =>
        value >= 0m && value <= VoucherInputPolicy.MaxStoredAmount &&
        VoucherInputPolicy.HasMoneyPrecision(value);

    private static BusinessException Conflict(string code) =>
        new(BusinessErrorType.Conflict, code);
}
