using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.ProductImages;

public sealed class ReorderProductImagesDto
{
    [Required]
    public List<Guid> ImageIds { get; set; } = [];
}
