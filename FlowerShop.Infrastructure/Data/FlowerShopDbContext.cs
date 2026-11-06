using FlowerShop.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Data;

public class FlowerShopDbContext : DbContext
{
    public FlowerShopDbContext(
        DbContextOptions<FlowerShopDbContext> options)
        : base(options)
    {
    }

    // User
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<UserAddress> UserAddresses { get; set; } = null!;

    // Product
    public DbSet<Category> Categories { get; set; } = null!;
    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<ProductImage> ProductImages { get; set; } = null!;

    // Cart
    public DbSet<Cart> Carts { get; set; } = null!;
    public DbSet<CartItem> CartItems { get; set; } = null!;

    // Order
    public DbSet<Order> Orders { get; set; } = null!;
    public DbSet<OrderItem> OrderItems { get; set; } = null!;

    // Payment
    public DbSet<Payment> Payments { get; set; } = null!;

    // Review
    public DbSet<Review> Reviews { get; set; } = null!;

    // Voucher
    public DbSet<Voucher> Vouchers { get; set; } = null!;

    // Notification
    public DbSet<Notification> Notifications { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(FlowerShopDbContext).Assembly);
    }
}
