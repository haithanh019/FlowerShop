namespace FlowerShop.Application.DTOs.Vouchers;

// Successful quote only. No usage reservation. Failures use normal API errors.
public sealed record VoucherPreviewResultDto(
    string VoucherCode,
    decimal Subtotal,
    decimal ShippingFee,
    decimal DiscountAmount,
    decimal TotalAmount,
    DateTime QuotedAtUtc);
