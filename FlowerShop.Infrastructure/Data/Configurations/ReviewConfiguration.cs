using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class ReviewConfiguration
    : BaseEntityConfiguration<Review>
{
    public override void Configure(
        EntityTypeBuilder<Review> builder)
    {
        base.Configure(builder);

        // Table & Check Constraints
        builder.ToTable("Reviews", table =>
        {
            table.HasCheckConstraint(
                "CK_Reviews_Rating_Range",
                "[Rating] BETWEEN 1 AND 5");
        });

        // Rating
        builder.Property(x => x.Rating)
            .IsRequired();

        // Comment
        builder.Property(x => x.Comment)
            .HasMaxLength(2000)
            .IsRequired(false);

        // Relationship: User (1) -> Review (N)
        builder.HasOne(x => x.User)
            .WithMany(x => x.Reviews)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Relationship: Product (1) -> Review (N)
        builder.HasOne(x => x.Product)
            .WithMany(x => x.Reviews)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Relationship: OrderItem (1) -> Review (0..1)
        builder.HasOne(x => x.OrderItem)
            .WithOne(x => x.Review)
            .HasForeignKey<Review>(x => x.OrderItemId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // One Review per OrderItem
        builder.HasIndex(x => x.OrderItemId)
            .IsUnique()
            .HasDatabaseName("UX_Reviews_OrderItemId");

        // Product Review List
        builder.HasIndex(x => new
        {
            x.ProductId,
            x.CreatedAt
        })
            .HasDatabaseName("IX_Reviews_ProductId_CreatedAt");

        // User Review History
        builder.HasIndex(x => new
        {
            x.UserId,
            x.CreatedAt
        })
            .HasDatabaseName("IX_Reviews_UserId_CreatedAt");
    }
}
