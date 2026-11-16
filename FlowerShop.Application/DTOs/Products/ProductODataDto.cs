using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Products;

public sealed class ProductODataDto
{
    [Key]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal Price { get; set; }

    public Guid CategoryId { get; set; }

    public bool IsInStock { get; set; }

    public DateTime CreatedAt { get; set; }
}
