using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Categories;

public abstract class CategoryUpsertDto
{
    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(180)]
    [RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }
}
