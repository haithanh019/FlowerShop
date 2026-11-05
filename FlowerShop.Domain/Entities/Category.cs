
using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class Category : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Navigation Property
    public ICollection<Product> Products { get; set; }
        = new List<Product>();
}
