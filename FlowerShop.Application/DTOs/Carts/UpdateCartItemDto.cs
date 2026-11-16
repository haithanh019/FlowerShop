using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Carts;

public sealed class UpdateCartItemDto
{
    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}
