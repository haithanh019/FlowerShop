using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class Review : BaseEntity
{
    // Foreign Keys
    public Guid UserId { get; set; }
    public Guid ProductId { get; set; }
    public Guid OrderItemId { get; set; }

    // Review Information
    public int Rating { get; set; }
    public string? Comment { get; set; }

    // Navigation Properties
    public User User { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public OrderItem OrderItem { get; set; } = null!;
}
