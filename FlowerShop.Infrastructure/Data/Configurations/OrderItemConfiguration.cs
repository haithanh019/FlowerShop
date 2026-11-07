using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class OrderItemConfiguration
    : BaseEntityConfiguration<OrderItem>
{
    public override void Configure(
        EntityTypeBuilder<OrderItem> builder)
    {
        base.Configure(builder);

        // Table & Check Constraints
        builder.ToTable("OrderItems", table =>
        {
            table.HasCheckConstraint(
                "CK_OrderItems_Quantity_Positive",
                "[Quantity] > 0");

            table.HasCheckConstraint(
                "CK_OrderItems_UnitPrice_Positive",
                "[UnitPrice] > 0");
        });

        // Product Snapshot
        builder.Property(x => x.ProductName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.UnitPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(x => x.Quantity)
            .IsRequired();

        // Calculated in C#, not stored in SQL
        builder.Ignore(x => x.LineTotal);

        // Relationship: Order (1) -> OrderItem (N)
        builder.HasOne(x => x.Order)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Relationship: Product (1) -> OrderItem (N)
        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Prevent duplicate products in the same Order
        builder.HasIndex(x => new
        {
            x.OrderId,
            x.ProductId
        })
            .IsUnique()
            .HasDatabaseName(
                "UX_OrderItems_OrderId_ProductId");

        // Product Reference Index
        builder.HasIndex(x => x.ProductId)
            .HasDatabaseName("IX_OrderItems_ProductId");
    }
}
