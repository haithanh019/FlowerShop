using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class UserAddressConfiguration
    : BaseEntityConfiguration<UserAddress>
{
    public override void Configure(
        EntityTypeBuilder<UserAddress> builder)
    {
        base.Configure(builder);

        // Table
        builder.ToTable("UserAddresses");

        // Recipient Information
        builder.Property(x => x.RecipientName)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.RecipientPhone)
            .HasMaxLength(20)
            .IsRequired();

        // Address Information
        builder.Property(x => x.AddressLine)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.Ward)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.District)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Province)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.CountryCode)
            .HasMaxLength(2)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(x => x.PostalCode)
            .HasMaxLength(20);

        // Default Address
        builder.Property(x => x.IsDefault)
            .IsRequired();

        // Relationship: User (1) -> UserAddress (N)
        builder.HasOne(x => x.User)
            .WithMany(x => x.Addresses)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // Index for retrieving user addresses
        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("IX_UserAddresses_UserId");

        // Only one default address per user
        builder.HasIndex(
                x => x.UserId,
                "UX_UserAddresses_OneDefault")
            .IsUnique()
            .HasFilter("[IsDefault] = 1");
    }
}
