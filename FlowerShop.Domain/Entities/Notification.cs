using FlowerShop.Domain.Common;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Entities;

public class Notification : BaseEntity
{
    // Foreign Key
    public Guid UserId { get; set; }

    // Notification Information
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    // Navigation Target
    public string? TargetUrl { get; set; }

    // Read Status
    public DateTime? ReadAt { get; set; }
    public bool IsRead => ReadAt.HasValue;

    // Navigation Property
    public User User { get; set; } = null!;
}
