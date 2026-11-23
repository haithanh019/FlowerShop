namespace FlowerShop.Application.DTOs.Orders;

public sealed record OrderHistoryDto(
    IReadOnlyList<OrderSummaryDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
