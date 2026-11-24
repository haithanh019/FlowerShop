namespace FlowerShop.Application.Common.Pricing;

public readonly record struct VoucherPrice(
    decimal Subtotal,
    decimal ShippingFee,
    decimal DiscountAmount,
    decimal TotalAmount);
