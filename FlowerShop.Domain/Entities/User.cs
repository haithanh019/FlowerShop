using FlowerShop.Domain.Common;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Entities;

public class User : BaseEntity
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }

    public UserRole Role { get; set; } = UserRole.Customer;

    public bool IsActive { get; set; } = true;
    public bool IsEmailVerified { get; set; } = false;

    // Navigation Property
    public ICollection<UserAddress> Addresses { get; set; }
        = new List<UserAddress>();
    public Cart? Cart { get; set; }
    public ICollection<Order> Orders { get; set; }
    = new List<Order>();
}
