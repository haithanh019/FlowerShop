namespace FlowerShop.Application.DTOs.Products;

public sealed class CreateProductDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal Price { get; set; }

    public int StockQuantity { get; set; }

    public Guid CategoryId { get; set; }

    public bool IsActive { get; set; } = false;
}
