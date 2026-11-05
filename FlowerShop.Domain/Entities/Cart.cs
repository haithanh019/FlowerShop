using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class Cart : BaseEntity
{
    // Foreign Key
    public Guid UserId { get; set; }

    // Navigation Properties
    public User User { get; set; } = null!;

    public ICollection<CartItem> Items { get; set; }
        = new List<CartItem>();
}
