using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed record OrderDetailDto(
    Guid OrderId,
    string OrderCode,
    OrderStatus Status,
    string RecipientName,
    string RecipientPhone,
    string ShippingAddressLine,
    string ShippingWard,
    string ShippingDistrict,
    string ShippingProvince,
    string ShippingCountryCode,
    string? ShippingPostalCode,
    string? Note,
    decimal Subtotal,
    decimal ShippingFee,
    decimal DiscountAmount,
    decimal TotalAmount,
    IReadOnlyList<OrderItemDto> Items,
    IReadOnlyList<OrderPaymentDto> Payments,
    DateTime CreatedAt);
