using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Products;

public sealed class StockChangeDto
{
    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}