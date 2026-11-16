using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Categories;

public sealed class CategoryODataDto
{
    [Key]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
