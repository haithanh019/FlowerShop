namespace FlowerShop.Application.DTOs.Carts;

public sealed record CartDto(
    Guid? CartId,
    IReadOnlyList<CartItemDto> Items,
    int TotalQuantity,
    decimal Subtotal);
