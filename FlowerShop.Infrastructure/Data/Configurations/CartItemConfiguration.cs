using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class CartItemConfiguration
    : BaseEntityConfiguration<CartItem>
{
    public override void Configure(
        EntityTypeBuilder<CartItem> builder)
    {
        base.Configure(builder);

        // Table & Check Constraints
        builder.ToTable("CartItems", table =>
        {
            table.HasCheckConstraint(
                "CK_CartItems_Quantity_Positive",
                "[Quantity] > 0");
        });

        // Properties
        builder.Property(x => x.Quantity)
            .IsRequired();

        // Relationship: Cart (1) -> CartItem (N)
        builder.HasOne(x => x.Cart)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.CartId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        // Relationship: Product (1) -> CartItem (N)
        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Prevent duplicate products in one Cart
        builder.HasIndex(x => new
        {
            x.CartId,
            x.ProductId
        })
            .IsUnique()
            .HasDatabaseName(
                "UX_CartItems_CartId_ProductId");

        // Index for Product queries
        builder.HasIndex(x => x.ProductId)
            .HasDatabaseName("IX_CartItems_ProductId");
    }
}
