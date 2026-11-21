using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed record CodPaymentSettlementResultDto(
    Guid OrderId,
    string OrderCode,
    Guid PaymentId,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    DateTime PaidAtUtc,
    DateTime UpdatedAtUtc);