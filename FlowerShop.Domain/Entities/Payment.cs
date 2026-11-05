using FlowerShop.Domain.Common;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Entities;

public class Payment : BaseEntity
{
    // Foreign Key
    public Guid OrderId { get; set; }

    // Payment Information
    public PaymentMethod Method { get; set; }
    public PaymentStatus Status { get; set; }
        = PaymentStatus.Pending;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "VND";

    // External Transaction
    public string? ProviderTransactionId { get; set; }
    public string? FailureReason { get; set; }

    // Payment Time
    public DateTime? PaidAt { get; set; }

    // Navigation Property
    public Order Order { get; set; } = null!;
}
