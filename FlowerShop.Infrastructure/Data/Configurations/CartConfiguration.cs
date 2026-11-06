using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class CartConfiguration
    : BaseEntityConfiguration<Cart>
{
    public override void Configure(EntityTypeBuilder<Cart> builder)
    {
        base.Configure(builder);

        // Table
        builder.ToTable("Carts");

        // Relationship: User (1) -> Cart (0..1)
        builder.HasOne(x => x.User)
            .WithOne(x => x.Cart)
            .HasForeignKey<Cart>(x => x.UserId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();

        // One Cart per User
        builder.HasIndex(x => x.UserId)
            .IsUnique()
            .HasDatabaseName("UX_Carts_UserId");
    }
}
