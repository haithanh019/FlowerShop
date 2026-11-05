using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class CartItem : BaseEntity
{
    // Foreign Keys
    public Guid CartId { get; set; }
    public Guid ProductId { get; set; }

    public int Quantity { get; set; } = 1;

    // Navigation Properties
    public Cart Cart { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
