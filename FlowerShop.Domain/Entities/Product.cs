
using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class Product : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }

    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; } = true;

    // Foreign Key
    public Guid CategoryId { get; set; }

    // Navigation Properties
    public Category Category { get; set; } = null!;

    public ICollection<ProductImage> Images { get; set; }
        = new List<ProductImage>();
}
