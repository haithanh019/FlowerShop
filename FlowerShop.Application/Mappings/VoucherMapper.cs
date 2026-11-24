using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Domain.Entities;

namespace FlowerShop.Application.Mappings;

public static class VoucherMapper
{
    public static VoucherDto ToDto(Voucher voucher) => new(
        VoucherId: voucher.Id,
        Code: voucher.Code,
        Description: voucher.Description,
        DiscountType: voucher.DiscountType,
        DiscountValue: voucher.DiscountValue,
        MinOrderAmount: voucher.MinOrderAmount,
        MaxDiscountAmount: voucher.MaxDiscountAmount,
        UsageLimit: voucher.UsageLimit,
        UsedCount: voucher.UsedCount,
        PerUserLimit: voucher.PerUserLimit,
        StartAtUtc: AsUtc(voucher.StartAt),
        EndAtUtc: AsUtc(voucher.EndAt),
        IsActive: voucher.IsActive,
        CreatedAtUtc: AsUtc(voucher.CreatedAt),
        UpdatedAtUtc: voucher.UpdatedAt is { } updated ? AsUtc(updated) : null);

    private static DateTime AsUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
