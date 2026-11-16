using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed record OrderSummaryDto(
    Guid OrderId,
    string OrderCode,
    OrderStatus Status,
    PaymentMethod? PaymentMethod,
    PaymentStatus? PaymentStatus,
    int TotalQuantity,
    decimal TotalAmount,
    DateTime CreatedAt);
