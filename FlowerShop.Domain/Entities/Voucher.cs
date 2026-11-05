using FlowerShop.Domain.Common;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Entities;

public class Voucher : BaseEntity
{
    // Voucher Information
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Discount
    public DiscountType DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public decimal MinOrderAmount { get; set; }
    public decimal? MaxDiscountAmount { get; set; }

    // Usage Limits
    public int? UsageLimit { get; set; }
    public int UsedCount { get; set; }
    public int? PerUserLimit { get; set; }

    // Validity
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation Property
    public ICollection<Order> Orders { get; set; }
        = new List<Order>();
}
