using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class OrderConfiguration
    : BaseEntityConfiguration<Order>
{
    public override void Configure(EntityTypeBuilder<Order> builder)
    {
        base.Configure(builder);

        // Table & Check Constraints
        builder.ToTable("Orders", table =>
        {
            table.HasCheckConstraint(
                "CK_Orders_Amounts_NonNegative",
                "[Subtotal] >= 0 AND " +
                "[ShippingFee] >= 0 AND " +
                "[DiscountAmount] >= 0 AND " +
                "[TotalAmount] >= 0");

            table.HasCheckConstraint(
                "CK_Orders_Discount_Valid",
                "[DiscountAmount] <= [Subtotal]");

            table.HasCheckConstraint(
                "CK_Orders_Total_Valid",
                "[TotalAmount] = " +
                "[Subtotal] + [ShippingFee] - [DiscountAmount]");
        });

        // Order Information
        builder.Property(x => x.OrderCode)
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        // Recipient Information
        builder.Property(x => x.RecipientName)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.RecipientPhone)
            .HasMaxLength(20)
            .IsRequired();

        // Shipping Address
        builder.Property(x => x.ShippingAddressLine)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.ShippingWard)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ShippingDistrict)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ShippingProvince)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ShippingCountryCode)
            .HasMaxLength(2)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(x => x.ShippingPostalCode)
            .HasMaxLength(20);

        builder.Property(x => x.Note)
            .HasMaxLength(1000);

        // Pricing
        builder.Property(x => x.Subtotal)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(x => x.ShippingFee)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(x => x.DiscountAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(x => x.TotalAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        // Relationship: User (1) -> Order (N)
        builder.HasOne(x => x.User)
            .WithMany(x => x.Orders)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Relationship: Voucher (1) -> Order (N)
        builder.HasOne(x => x.Voucher)
            .WithMany(x => x.Orders)
            .HasForeignKey(x => x.VoucherId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired(false);

        // Unique Order Code
        builder.HasIndex(x => x.OrderCode)
            .IsUnique()
            .HasDatabaseName("UX_Orders_OrderCode");

        // User Order History
        builder.HasIndex(x => new
        {
            x.UserId,
            x.CreatedAt
        })
            .HasDatabaseName(
                "IX_Orders_UserId_CreatedAt");

        // Voucher Index
        builder.HasIndex(x => x.VoucherId)
            .HasDatabaseName("IX_Orders_VoucherId");
    }
}
