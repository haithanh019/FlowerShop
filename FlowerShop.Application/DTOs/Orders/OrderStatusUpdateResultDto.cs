using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed record OrderStatusUpdateResultDto(
    Guid OrderId,
    string OrderCode,
    OrderStatus PreviousStatus,
    OrderStatus Status,
    DateTime UpdatedAtUtc);
