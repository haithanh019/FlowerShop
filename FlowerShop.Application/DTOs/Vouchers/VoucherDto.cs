using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Vouchers;

// Admin response. Customer availability will use a separate public projection.
public sealed record VoucherDto(
    Guid VoucherId,
    string Code,
    string? Description,
    DiscountType DiscountType,
    decimal DiscountValue,
    decimal MinOrderAmount,
    decimal? MaxDiscountAmount,
    int? UsageLimit,
    int UsedCount,
    int? PerUserLimit,
    DateTime StartAtUtc,
    DateTime EndAtUtc,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);
