namespace FlowerShop.Application.DTOs.ProductImages;

public sealed record ProductImageDto(
    Guid Id,
    Guid ProductId,
    string ImageUrl,
    bool IsPrimary,
    int SortOrder,
    DateTime CreatedAt);
