using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class UserConfiguration
    : BaseEntityConfiguration<User>
{
    public override void Configure(EntityTypeBuilder<User> builder)
    {
        base.Configure(builder);

        // Table
        builder.ToTable("Users");

        // Full Name
        builder.Property(x => x.FullName)
            .HasMaxLength(150)
            .IsRequired();

        // Email
        builder.Property(x => x.Email)
            .HasMaxLength(256)
            .IsRequired();

        // Password
        builder.Property(x => x.PasswordHash)
            .HasMaxLength(512)
            .IsRequired();

        // Phone Number
        builder.Property(x => x.PhoneNumber)
            .HasMaxLength(20);

        // Role
        builder.Property(x => x.Role)
            .HasConversion<int>()
            .IsRequired();

        // Status
        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.Property(x => x.IsEmailVerified)
            .IsRequired();

        // Unique Index
        builder.HasIndex(x => x.Email)
            .IsUnique()
            .HasDatabaseName("UX_Users_Email");
    }
}
