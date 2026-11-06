using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class ProductConfiguration
    : BaseEntityConfiguration<Product>
{
    public override void Configure(EntityTypeBuilder<Product> builder)
    {
        base.Configure(builder);

        // Table & Check Constraints
        builder.ToTable("Products", table =>
        {
            table.HasCheckConstraint(
                "CK_Products_Price_Positive",
                "[Price] > 0");

            table.HasCheckConstraint(
                "CK_Products_Stock_NonNegative",
                "[StockQuantity] >= 0");
        });

        // Properties
        builder.Property(x => x.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Slug)
            .HasMaxLength(250)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(4000);

        builder.Property(x => x.Price)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(x => x.StockQuantity)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .IsRequired();

        // Relationship: Category (1) -> Product (N)
        builder.HasOne(x => x.Category)
            .WithMany(x => x.Products)
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Indexes
        builder.HasIndex(x => x.Slug)
            .IsUnique()
            .HasDatabaseName("UX_Products_Slug");

        builder.HasIndex(x => x.CategoryId)
            .HasDatabaseName("IX_Products_CategoryId");
    }
}
