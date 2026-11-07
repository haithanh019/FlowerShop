using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public sealed class CategoryConfiguration
    : BaseEntityConfiguration<Category>
{
    public override void Configure(EntityTypeBuilder<Category> builder)
    {
        base.Configure(builder);

        // Table
        builder.ToTable("Categories");

        // Properties
        builder.Property(x => x.Name)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.Slug)
            .HasMaxLength(180)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(1000);

        // Unique Index
        builder.HasIndex(x => x.Slug)
            .IsUnique()
            .HasDatabaseName("UX_Categories_Slug");
    }
}
