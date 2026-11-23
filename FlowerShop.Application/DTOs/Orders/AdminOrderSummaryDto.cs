using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed record AdminOrderSummaryDto(
    Guid OrderId,
    string OrderCode,
    Guid CustomerUserId,
    string CustomerEmail,
    string RecipientName,
    OrderStatus Status,
    PaymentMethod? PaymentMethod,
    PaymentStatus? PaymentStatus,
    int TotalQuantity,
    decimal TotalAmount,
    DateTime CreatedAt);
