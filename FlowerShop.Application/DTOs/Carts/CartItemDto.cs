namespace FlowerShop.Application.DTOs.Carts;

public sealed record CartItemDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    string ProductSlug,
    string? PrimaryImageUrl,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    bool IsAvailable);
