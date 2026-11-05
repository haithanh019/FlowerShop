using FlowerShop.Domain.Common;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Entities;

public class Order : BaseEntity
{
    // Order Information
    public string OrderCode { get; set; } = string.Empty;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    // Foreign Key
    public Guid UserId { get; set; }

    // Shipping Information
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientPhone { get; set; } = string.Empty;

    public string ShippingAddressLine { get; set; } = string.Empty;
    public string ShippingWard { get; set; } = string.Empty;
    public string ShippingDistrict { get; set; } = string.Empty;
    public string ShippingProvince { get; set; } = string.Empty;
    public string ShippingCountryCode { get; set; } = string.Empty;
    public string? ShippingPostalCode { get; set; }

    public string? Note { get; set; }

    // Pricing
    public decimal Subtotal { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }

    // Navigation Properties
    public User User { get; set; } = null!;

    public ICollection<OrderItem> Items { get; set; }
        = new List<OrderItem>();
    public ICollection<Payment> Payments { get; set; }
    = new List<Payment>();
}
