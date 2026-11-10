namespace FlowerShop.Application.DTOs.Products;

public sealed class UpdateProductDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal Price { get; set; }

    public Guid CategoryId { get; set; }

    public bool IsActive { get; set; }
}
