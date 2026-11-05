using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class OrderItem : BaseEntity
{
    // Foreign Keys
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }

    // Product Snapshot
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }

    public decimal LineTotal => UnitPrice * Quantity;

    // Navigation Properties
    public Order Order { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public Review? Review { get; set; }
}
