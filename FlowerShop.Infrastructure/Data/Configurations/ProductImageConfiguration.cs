using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class ProductImageConfiguration
    : BaseEntityConfiguration<ProductImage>
{
    public override void Configure(
        EntityTypeBuilder<ProductImage> builder)
    {
        base.Configure(builder);

        // Table & Check Constraints
        builder.ToTable("ProductImages", table =>
        {
            table.HasCheckConstraint(
                "CK_ProductImages_SortOrder_NonNegative",
                "[SortOrder] >= 0");
        });

        // Image URL
        builder.Property(x => x.ImageUrl)
            .HasMaxLength(2048)
            .IsRequired();

        // Cloudinary Public ID
        builder.Property(x => x.PublicId)
            .HasMaxLength(512);

        // Primary Image
        builder.Property(x => x.IsPrimary)
            .IsRequired();

        // Display Order
        builder.Property(x => x.SortOrder)
            .IsRequired();

        // Relationship: Product (1) -> ProductImage (N)
        builder.HasOne(x => x.Product)
            .WithMany(x => x.Images)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Index for sorting images by Product
        builder.HasIndex(x => new
        {
            x.ProductId,
            x.SortOrder
        })
            .HasDatabaseName(
                "IX_ProductImages_ProductId_SortOrder");

        // Only one primary image per Product
        builder.HasIndex(
                x => x.ProductId,
                "UX_ProductImages_OnePrimary")
            .IsUnique()
            .HasFilter("[IsPrimary] = 1");
    }
}
