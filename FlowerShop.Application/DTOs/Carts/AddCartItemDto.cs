using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Carts;

public sealed class AddCartItemDto
{
    public Guid ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}
