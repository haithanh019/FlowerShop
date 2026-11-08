using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations
{
    public class NotificationConfiguration
     : BaseEntityConfiguration<Notification>
    {
        public override void Configure(
            EntityTypeBuilder<Notification> builder)
        {
            base.Configure(builder);

            // Table name and check constraints
            builder.ToTable("Notifications", table =>
            {
                table.HasCheckConstraint(
                    "CK_Notifications_Type",
                    "[Type] IN (1, 2, 3, 4)");
            });

            // UserId
            builder.Property(x => x.UserId)
                .IsRequired();

            // Type
            builder.Property(x => x.Type)
                .HasConversion<int>()
                .IsRequired();

            // Title
            builder.Property(x => x.Title)
                .HasMaxLength(200)
                .IsRequired();

            // Content
            builder.Property(x => x.Content)
                .HasMaxLength(4000)
                .IsRequired();

            // TargetUrl
            builder.Property(x => x.TargetUrl)
                .HasMaxLength(2048)
                .IsRequired(false);

            // ReadAt
            builder.Property(x => x.ReadAt)
                .HasPrecision(3)
                .IsRequired(false);

            // IsRead is a calculated property.
            // It is derived from ReadAt and should not be stored.
            builder.Ignore(x => x.IsRead);

            // Relationship: User 1 - N Notifications
            builder.HasOne(x => x.User)
                .WithMany(x => x.Notifications)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.NoAction)
                .IsRequired();

            // General index
            builder.HasIndex(
                x => new { x.UserId, x.CreatedAt },
                "IX_Notifications_UserId_CreatedAt");

            // Filtered index
            builder.HasIndex(
                x => new { x.UserId, x.CreatedAt },
                "IX_Notifications_Unread_UserId_CreatedAt")
                .HasFilter("[ReadAt] IS NULL");
        }
    }
}
