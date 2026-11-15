using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Products;

public sealed class CreateProductDto : ProductUpsertDto
{
    [Range(0, int.MaxValue)]
    public int StockQuantity { get; set; }

    public bool IsActive { get; set; } = false;
}
