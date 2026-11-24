using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Vouchers;

public sealed record CustomerVoucherDto(
    string Code,
    string? Description,
    DiscountType DiscountType,
    decimal DiscountValue,
    decimal MinOrderAmount,
    decimal? MaxDiscountAmount,
    int? PerUserLimit,
    DateTime StartAtUtc,
    DateTime EndAtUtc);
