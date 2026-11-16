
using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Products;

public abstract class ProductUpsertDto
{
    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(250)]
    [RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal Price { get; set; }

    public Guid CategoryId { get; set; }
}
