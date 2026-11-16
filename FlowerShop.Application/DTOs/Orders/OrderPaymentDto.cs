using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed record OrderPaymentDto(
    Guid PaymentId,
    PaymentMethod Method,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    DateTime? PaidAt,
    DateTime CreatedAt);
