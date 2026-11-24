namespace FlowerShop.Application.DTOs.Vouchers;

public sealed record CustomerVoucherPageDto(
    IReadOnlyList<CustomerVoucherDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    DateTime EvaluatedAtUtc);
