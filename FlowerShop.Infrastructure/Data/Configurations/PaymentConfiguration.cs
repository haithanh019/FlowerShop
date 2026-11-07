using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class PaymentConfiguration
    : BaseEntityConfiguration<Payment>
{
    public override void Configure(
        EntityTypeBuilder<Payment> builder)
    {
        base.Configure(builder);

        // Table & Check Constraints
        builder.ToTable("Payments", table =>
        {
            table.HasCheckConstraint(
                "CK_Payments_Amount_Positive",
                "[Amount] > 0");

            table.HasCheckConstraint(
                "CK_Payments_Method_Valid",
                "[Method] IN (1, 2, 3, 4)");

            table.HasCheckConstraint(
                "CK_Payments_Status_Valid",
                "[Status] BETWEEN 0 AND 5");
        });

        // Payment Method
        builder.Property(x => x.Method)
            .HasConversion<int>()
            .IsRequired();

        // Payment Status
        builder.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        // Amount
        builder.Property(x => x.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        // Currency
        builder.Property(x => x.Currency)
            .HasMaxLength(3)
            .IsUnicode(false)
            .IsRequired();

        // External Transaction
        builder.Property(x => x.ProviderTransactionId)
            .HasMaxLength(256);

        builder.Property(x => x.FailureReason)
            .HasMaxLength(1000);

        // Payment Time
        builder.Property(x => x.PaidAt)
            .HasPrecision(3);

        // Relationship: Order (1) -> Payment (N)
        builder.HasOne(x => x.Order)
            .WithMany(x => x.Payments)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Payment History by Order
        builder.HasIndex(x => new
        {
            x.OrderId,
            x.CreatedAt
        })
            .HasDatabaseName(
                "IX_Payments_OrderId_CreatedAt");

        // Unique Gateway Transaction
        builder.HasIndex(x => new
        {
            x.Method,
            x.ProviderTransactionId
        })
            .IsUnique()
            .HasFilter(
                "[ProviderTransactionId] IS NOT NULL")
            .HasDatabaseName(
                "UX_Payments_Method_TransactionId");

        // Only one settled Payment per Order
        builder.HasIndex(
                x => x.OrderId,
                "UX_Payments_OneSettledPerOrder")
            .IsUnique()
            .HasFilter("[Status] IN (2, 5)");
    }
}
