using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Vouchers;

public sealed record AdminVoucherViewDto(
    VoucherDto Voucher,
    VoucherAvailabilityStatus Status,
    DateTime EvaluatedAtUtc);
