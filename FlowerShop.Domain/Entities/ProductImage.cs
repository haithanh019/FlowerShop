using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class ProductImage : BaseEntity
{
    public string ImageUrl { get; set; } = string.Empty;
    public string? PublicId { get; set; }

    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }

    // Foreign Key
    public Guid ProductId { get; set; }

    // Navigation Property
    public Product Product { get; set; } = null!;
}
