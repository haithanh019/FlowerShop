using FlowerShop.Domain.Entities;
using FlowerShop.Infrastructure.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class VoucherConfiguration : BaseEntityConfiguration<Voucher>
{
    public override void Configure(EntityTypeBuilder<Voucher> builder)
    {
        base.Configure(builder);

        // Table name and check constraints
        builder.ToTable("Vouchers", table =>
        {
            // DiscountType must be Percentage (1) or FixedAmount (2)
            table.HasCheckConstraint(
                "CK_Vouchers_DiscountType",
                "[DiscountType] IN (1, 2)");

            // Percentage must be greater than 0 and at most 100.
            // FixedAmount must be greater than 0.
            table.HasCheckConstraint(
                "CK_Vouchers_DiscountValue",
                "[DiscountValue] > 0 AND " +
                "(([DiscountType] = 1 AND [DiscountValue] <= 100) " +
                "OR [DiscountType] = 2)");

            // Minimum order amount cannot be negative.
            table.HasCheckConstraint(
                "CK_Vouchers_MinOrderAmount",
                "[MinOrderAmount] >= 0");

            // Maximum discount amount must be null or greater than 0.
            table.HasCheckConstraint(
                "CK_Vouchers_MaxDiscountAmount",
                "[MaxDiscountAmount] IS NULL OR [MaxDiscountAmount] > 0");

            // Usage limit must be null or greater than 0.
            table.HasCheckConstraint(
                "CK_Vouchers_UsageLimit",
                "[UsageLimit] IS NULL OR [UsageLimit] > 0");

            // Used count cannot be negative.
            table.HasCheckConstraint(
                "CK_Vouchers_UsedCount",
                "[UsedCount] >= 0");

            // Per-user limit must be null or greater than 0.
            table.HasCheckConstraint(
                "CK_Vouchers_PerUserLimit",
                "[PerUserLimit] IS NULL OR [PerUserLimit] > 0");

            // Start date must be earlier than end date.
            table.HasCheckConstraint(
                "CK_Vouchers_DateRange",
                "[StartAt] < [EndAt]");

            // Used count must not exceed usage limit if usage limit is set.
            table.HasCheckConstraint(
                "CK_Vouchers_UsedCount_Limit",
                "[UsageLimit] IS NULL OR [UsedCount] <= [UsageLimit]");
        });

        // Code
        builder.Property(x => x.Code)
            .HasMaxLength(50)
            .IsRequired();

        // Description
        builder.Property(x => x.Description)
            .HasMaxLength(500)
            .IsRequired(false);

        // DiscountType
        builder.Property(x => x.DiscountType)
            .HasConversion<int>()
            .IsRequired();

        // DiscountValue
        builder.Property(x => x.DiscountValue)
            .HasPrecision(18, 2)
            .IsRequired();

        // MinOrderAmount
        builder.Property(x => x.MinOrderAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        // MaxDiscountAmount
        builder.Property(x => x.MaxDiscountAmount)
            .HasPrecision(18, 2)
            .IsRequired(false);

        // UsageLimit
        builder.Property(x => x.UsageLimit)
            .IsRequired(false);

        // UsedCount
        builder.Property(x => x.UsedCount)
            .HasDefaultValue(0)
            .IsRequired();

        // PerUserLimit
        builder.Property(x => x.PerUserLimit)
            .IsRequired(false);

        // StartAt
        builder.Property(x => x.StartAt)
            .HasPrecision(3)
            .IsRequired();

        // EndAt
        builder.Property(x => x.EndAt)
            .HasPrecision(3)
            .IsRequired();

        // IsActive
        builder.Property(x => x.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        // Indexes
        builder.HasIndex(x => x.Code)
            .IsUnique()
            .HasDatabaseName("UX_Vouchers_Code");

        builder.HasIndex(x => new
        {
            x.IsActive,
            x.StartAt,
            x.EndAt
        })
            .HasDatabaseName("IX_Vouchers_IsActive_StartAt_EndAt");
    }
}