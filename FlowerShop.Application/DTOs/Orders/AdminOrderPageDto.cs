namespace FlowerShop.Application.DTOs.Orders;

public sealed record AdminOrderPageDto(
    IReadOnlyList<AdminOrderSummaryDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
