using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Entities;

public class UserAddress : BaseEntity
{
    public Guid UserId { get; set; }

    public string RecipientName { get; set; } = string.Empty;
    public string RecipientPhone { get; set; } = string.Empty;

    public string AddressLine { get; set; } = string.Empty;
    public string Ward { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string? PostalCode { get; set; }

    public bool IsDefault { get; set; } = false;

    // Navigation Property
    public User User { get; set; } = null!;
}
