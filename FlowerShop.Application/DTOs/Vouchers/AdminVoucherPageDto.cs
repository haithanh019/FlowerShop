namespace FlowerShop.Application.DTOs.Vouchers;

public sealed record AdminVoucherPageDto(
    IReadOnlyList<AdminVoucherViewDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    DateTime EvaluatedAtUtc);
