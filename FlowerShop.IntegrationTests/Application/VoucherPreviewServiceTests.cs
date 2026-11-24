using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class VoucherPreviewServiceTests
{
    [Fact]
    public async Task Preview_ShouldUseCurrentCartPricesWithoutMutatingData()
    {
        await WithDatabaseAsync(async s =>
        {
            var first = await s.PreviewAsync(s.Customer.Id, $" {s.Voucher.Code.ToLowerInvariant()} ");
            Assert.Equal(s.Voucher.Code, first.VoucherCode);
            Assert.Equal(500000m, first.Subtotal);
            Assert.Equal(0m, first.ShippingFee);
            Assert.Equal(80000m, first.DiscountAmount);
            Assert.Equal(420000m, first.TotalAmount);
            Assert.Equal(DateTimeKind.Utc, first.QuotedAtUtc.Kind);
            Assert.Empty(s.Db.ChangeTracker.Entries());

            // A price change after the first quote must be reflected in the next quote.
            await s.Db.Products.Where(x => x.Id == s.Rose.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Price, 50000m));
            var next = await s.PreviewAsync(s.Customer.Id, s.Voucher.Code);
            Assert.Equal(300000m, next.Subtotal);
            Assert.Equal(60000m, next.DiscountAmount);
            Assert.Equal(240000m, next.TotalAmount);

            var persisted = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
            Assert.Equal(0, persisted.UsedCount);
            Assert.Null(persisted.UpdatedAt);
            Assert.Equal(10, await s.Db.Products.Where(x => x.Id == s.Rose.Id)
                .Select(x => x.StockQuantity).SingleAsync());
            Assert.Equal(10, await s.Db.Products.Where(x => x.Id == s.Lily.Id)
                .Select(x => x.StockQuantity).SingleAsync());
            var items = await s.Db.CartItems.AsNoTracking().Where(x => x.CartId == s.Cart.Id).ToListAsync();
            Assert.Equal(2, items.Count);
            Assert.Equal(2, Assert.Single(items, x => x.ProductId == s.Rose.Id).Quantity);
            Assert.Equal(1, Assert.Single(items, x => x.ProductId == s.Lily.Id).Quantity);
            Assert.False(await s.Db.Orders.AnyAsync(x => x.UserId == s.Customer.Id));
            Assert.Empty(s.Db.ChangeTracker.Entries());
        });
    }

    [Fact]
    public async Task Preview_ShouldRejectUnavailableCartAndUnauthorizedCustomer()
    {
        await WithDatabaseAsync(async s =>
        {
            await ExpectAsync(BusinessErrorType.Unauthorized, "CUSTOMER_ACCESS_REQUIRED",
                () => s.PreviewAsync(s.Admin.Id, s.Voucher.Code));
            await ExpectAsync(BusinessErrorType.Unauthorized, "CUSTOMER_ACCESS_REQUIRED",
                () => s.PreviewAsync(s.Inactive.Id, s.Voucher.Code));
            await ExpectAsync(BusinessErrorType.Unauthorized, "CUSTOMER_ACCESS_REQUIRED",
                () => s.PreviewAsync(Guid.NewGuid(), s.Voucher.Code));
            await ExpectAsync(BusinessErrorType.Conflict, "CART_EMPTY",
                () => s.PreviewAsync(s.Other.Id, s.Voucher.Code));
            await ExpectAsync(BusinessErrorType.NotFound, "VOUCHER_NOT_FOUND",
                () => s.PreviewAsync(s.Customer.Id, $"MISSING-{Guid.NewGuid():N}"));
            var invalid = await Assert.ThrowsAsync<ValidationException>(
                () => s.PreviewAsync(Guid.Empty, s.Voucher.Code));
            Assert.Equal("INVALID_USER_ID", invalid.Message);
            await Assert.ThrowsAsync<ValidationException>(() => s.PreviewAsync(s.Customer.Id, "bad code"));

            // One bad item invalidates the entire cart, even when the other is available.
            await s.Db.Products.Where(x => x.Id == s.Lily.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsActive, false));
            await ExpectAsync(BusinessErrorType.Conflict, "PRODUCT_INACTIVE",
                () => s.PreviewAsync(s.Customer.Id, s.Voucher.Code));
            await s.Db.Products.Where(x => x.Id == s.Lily.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsActive, true)
                    .SetProperty(x => x.StockQuantity, 0));
            await ExpectAsync(BusinessErrorType.Conflict, "INSUFFICIENT_STOCK",
                () => s.PreviewAsync(s.Customer.Id, s.Voucher.Code));
            Assert.Equal(0, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .Select(x => x.UsedCount).SingleAsync());
            Assert.Empty(s.Db.ChangeTracker.Entries());
        });
    }

    [Fact]
    public async Task Preview_ShouldCountOnlyNonCancelledOrdersAndRejectInconsistentUsage()
    {
        await WithDatabaseAsync(async s =>
        {
            // Seed historical order headers; actual redemption will be tested in Checkout.
            s.Db.Orders.AddRange(NewOrder(s.Customer.Id, s.Voucher.Id, OrderStatus.Cancelled),
                NewOrder(s.Other.Id, s.Voucher.Id, OrderStatus.Delivered));
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
            await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedCount, 1)
                    .SetProperty(x => x.PerUserLimit, 1));

            // Customer has one cancelled order, but no current usage. Another user's
            // delivered order counts globally without consuming this customer's limit.
            Assert.Equal(80000m, (await s.PreviewAsync(s.Customer.Id, s.Voucher.Code)).DiscountAmount);

            s.Db.Orders.Add(NewOrder(s.Customer.Id, s.Voucher.Id, OrderStatus.Pending));
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
            await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedCount, 2));
            await ExpectAsync(BusinessErrorType.Conflict, "VOUCHER_PER_USER_LIMIT_REACHED",
                () => s.PreviewAsync(s.Customer.Id, s.Voucher.Code));

            await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsageLimit, 2));
            await ExpectAsync(BusinessErrorType.Conflict, "VOUCHER_USAGE_LIMIT_REACHED",
                () => s.PreviewAsync(s.Customer.Id, s.Voucher.Code));

            await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedCount, 1));
            await ExpectAsync(BusinessErrorType.Conflict, "VOUCHER_USAGE_INCONSISTENT",
                () => s.PreviewAsync(s.Customer.Id, s.Voucher.Code));
            Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .Select(x => x.UsedCount).SingleAsync());
            Assert.Empty(s.Db.ChangeTracker.Entries());
        });
    }

    private static Order NewOrder(Guid userId, Guid voucherId, OrderStatus status) => new()
    {
        UserId = userId, VoucherId = voucherId, Status = status,
        OrderCode = $"VP-{Guid.NewGuid():N}", RecipientName = "Voucher Test",
        RecipientPhone = "0901234567", ShippingAddressLine = "123 Test Street",
        ShippingWard = "Ward", ShippingDistrict = "District", ShippingProvince = "Da Nang",
        ShippingCountryCode = "VN", Subtotal = 500000m,
        DiscountAmount = 80000m, TotalAmount = 420000m
    };

    private static async Task ExpectAsync(BusinessErrorType type, string code,
        Func<Task<VoucherPreviewResultDto>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(() => action());
        Assert.Equal(type, ex.ErrorType);
        Assert.Equal(code, ex.Message);
    }

    private static async Task WithDatabaseAsync(Func<TestState, Task> action)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test", StringComparison.OrdinalIgnoreCase) ||
            sql.MultipleActiveResultSets)
            throw new InvalidOperationException("Use FlowerShop_Test with MARS disabled.");
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IVoucherPreviewService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), x => x.EndsWith("_InitialCreate"));

        User NewUser(UserRole role = UserRole.Customer, bool active = true)
        {
            var user = new User
            {
                FullName = "Voucher Preview Test", Email = $"vp-{Guid.NewGuid():N}@example.invalid",
                Role = role, IsActive = active
            };
            user.PasswordHash = hasher.HashPassword(user, "FlowerShop#2026");
            return user;
        }

        var customer = NewUser();
        var other = NewUser();
        var admin = NewUser(UserRole.Admin);
        var inactive = NewUser(active: false);
        var category = new Category { Name = "Voucher Test", Slug = $"vp-cat-{Guid.NewGuid():N}" };
        var rose = new Product { Name = "Rose", Slug = $"vp-rose-{Guid.NewGuid():N}",
            CategoryId = category.Id, Price = 150000m, StockQuantity = 10, IsActive = true };
        var lily = new Product { Name = "Lily", Slug = $"vp-lily-{Guid.NewGuid():N}",
            CategoryId = category.Id, Price = 200000m, StockQuantity = 10, IsActive = true };
        var cart = new Cart { UserId = customer.Id };
        var voucher = new Voucher
        {
            Code = $"VP-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, MaxDiscountAmount = 80000m, MinOrderAmount = 300000m,
            UsageLimit = 10, UsedCount = 0, IsActive = true,
            StartAt = DateTime.UtcNow.AddDays(-1), EndAt = DateTime.UtcNow.AddDays(1)
        };

        // Preview opens no transaction; all seeded data rolls back after the test.
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            db.AddRange(customer, other, admin, inactive, category, rose, lily, cart, voucher);
            db.CartItems.AddRange(new CartItem { CartId = cart.Id, ProductId = rose.Id, Quantity = 2 },
                new CartItem { CartId = cart.Id, ProductId = lily.Id, Quantity = 1 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await action(new TestState(db, service, customer, other, admin, inactive, cart, rose, lily, voucher));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private sealed record TestState(FlowerShopDbContext Db, IVoucherPreviewService Service,
        User Customer, User Other, User Admin, User Inactive, Cart Cart, Product Rose, Product Lily, Voucher Voucher)
    {
        public Task<VoucherPreviewResultDto> PreviewAsync(Guid userId, string code) =>
            Service.PreviewAsync(userId, new VoucherPreviewRequestDto { VoucherCode = code });
    }
}
