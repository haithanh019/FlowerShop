using FlowerShop.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowerShop.Infrastructure.Data.Configurations;

public abstract class BaseEntityConfiguration<T>
    : IEntityTypeConfiguration<T>
    where T : BaseEntity
{
    public virtual void Configure(EntityTypeBuilder<T> builder)
    {
        // Primary Key
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .ValueGeneratedNever();

        // Created Date
        builder.Property(x => x.CreatedAt)
            .HasPrecision(3)
            .IsRequired();

        // Updated Date
        builder.Property(x => x.UpdatedAt)
            .HasPrecision(3);
    }
}
