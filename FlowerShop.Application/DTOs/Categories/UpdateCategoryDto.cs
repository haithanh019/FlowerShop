namespace FlowerShop.Application.DTOs.Categories;

public sealed class UpdateCategoryDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }
}
