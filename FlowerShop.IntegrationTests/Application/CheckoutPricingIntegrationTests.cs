using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Application.Common.Concurrency;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class CheckoutPricingIntegrationTests
{
    [Fact]
    public async Task Prepare_ShouldCalculateNoVoucherPriceWithoutOpeningTransaction()
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var pricing = scope.ServiceProvider.GetRequiredService<ICheckoutPricingService>();
        var id = Guid.NewGuid();

        foreach (var code in new string?[] { null, "", " \t " })
        {
            var prepared = await pricing.PrepareAsync(id, code, 500000m);
            Assert.Null(prepared.VoucherId);
            Assert.Equal(new VoucherPrice(500000m, 0m, 0m, 500000m), prepared.Price);
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => pricing.PrepareAsync(id, null, 0m));
        Assert.Equal("CHECKOUT_TOTAL_OUT_OF_RANGE", ex.Message);
        await Assert.ThrowsAsync<ValidationException>(() => pricing.PrepareAsync(id, "bad code", 500000m));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Prepare_ShouldUseFreshVoucherUnderSharedLockWithoutRedeeming()
    {
        await WithDatabaseAsync(async s =>
        {
            using var scope = s.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var pricing = scope.ServiceProvider.GetRequiredService<ICheckoutPricingService>();

            // A deliberately stale tracked entity must not be used for pricing.
            // In normal Checkout the voucher is never tracked before acquiring its lock.
            var stale = await db.Vouchers.SingleAsync(x => x.Id == s.Voucher.Id);
            Assert.Equal(20m, stale.DiscountValue);
            Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.DiscountValue, 30m)));

            var cartKey = $"flower-shop:cart:{s.Customer.Id:N}";
            var voucherKey = VoucherLockKeys.ForId(s.Voucher.Id);
            var prepared = await uow.ExecuteInLockedTransactionAsync(cartKey, async ct =>
            {
                var transaction = db.Database.CurrentTransaction;
                Assert.NotNull(transaction);
                Assert.Equal("NoLock", await ReadModeAsync(db, voucherKey, ct));
                var result = await pricing.PrepareAsync(s.Customer.Id,
                    $" {s.Voucher.Code.ToLowerInvariant()} ", 500000m, ct);
                Assert.Same(transaction, db.Database.CurrentTransaction);
                Assert.Equal("Exclusive", await ReadModeAsync(db, cartKey, ct));
                Assert.Equal("Exclusive", await ReadModeAsync(db, voucherKey, ct));
                Assert.Equal(s.Voucher.Id, result.VoucherId);
                // 30% of 500000 = 150000, capped at 120000.
                Assert.Equal(new VoucherPrice(500000m, 0m, 120000m, 380000m), result.Price);
                Assert.Equal(20m, stale.DiscountValue);
                Assert.Equal(EntityState.Unchanged, db.Entry(stale).State);
                return result;
            });
            Assert.Equal(380000m, prepared.Price.TotalAmount);
            Assert.Null(db.Database.CurrentTransaction);
            await AssertUnredeemedAsync(s);
        });
    }

    [Fact]
    public async Task Prepare_ShouldRequireTransactionAndRespectCommittedUsage()
    {
        await WithDatabaseAsync(async s =>
        {
            using (var scope = s.Factory.Services.CreateScope())
            {
                var pricing = scope.ServiceProvider.GetRequiredService<ICheckoutPricingService>();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    pricing.PrepareAsync(s.Customer.Id, s.Voucher.Code, 500000m));
                Assert.Equal("TRANSACTION_REQUIRED", ex.Message);
            }

            // Historical headers suffice for usage-count testing; not actual Checkout.
            s.Db.Orders.Add(NewHistory(s.Customer.Id, s.Voucher.Id, OrderStatus.Cancelled));
            s.Db.Orders.Add(NewHistory(s.Other.Id, s.Voucher.Id, OrderStatus.Delivered));
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
            await SetCountAsync(s, 1);

            Assert.Equal(100000m, (await PrepareLockedAsync(s, s.Customer.Id, s.Voucher.Code)).Price.DiscountAmount);
            await ExpectBusinessAsync("VOUCHER_PER_USER_LIMIT_REACHED", () =>
                PrepareLockedAsync(s, s.Other.Id, s.Voucher.Code));
            await ExpectBusinessAsync("VOUCHER_MIN_ORDER_NOT_MET", () =>
                PrepareLockedAsync(s, s.Customer.Id, s.Voucher.Code, 100000m));
            await ExpectBusinessAsync("VOUCHER_NOT_FOUND", () =>
                PrepareLockedAsync(s, s.Customer.Id, $"UNKNOWN-{Guid.NewGuid():N}"));

            await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.UsageLimit, 1));
            await ExpectBusinessAsync("VOUCHER_USAGE_LIMIT_REACHED", () =>
                PrepareLockedAsync(s, s.Customer.Id, s.Voucher.Code));

            await SetCountAsync(s, 0);
            await ExpectBusinessAsync("VOUCHER_USAGE_INCONSISTENT", () =>
                PrepareLockedAsync(s, s.Customer.Id, s.Voucher.Code));
            await SetCountAsync(s, 1);
            await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.UsageLimit, 10)
                    .SetProperty(x => x.IsActive, false));
            await ExpectBusinessAsync("VOUCHER_INACTIVE", () =>
                PrepareLockedAsync(s, s.Customer.Id, s.Voucher.Code));

            Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id).Select(x => x.UsedCount).SingleAsync());
            Assert.Equal(2, await s.Db.Orders.CountAsync(x => x.VoucherId == s.Voucher.Id));
            Assert.Equal(10, await s.Db.Products.Where(x => x.Id == s.Product.Id).Select(x => x.StockQuantity).SingleAsync());
        });
    }

    [Fact]
    public async Task CheckoutHttp_RolloutGuard_ShouldBlockNewVoucherAndPreserveV1Replay()
    {
        await WithDatabaseAsync(async s =>
        {
            using var client = s.Factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
            using var login = await client.PostAsJsonAsync("/api/auth/login",
                new LoginDto { Email = s.Customer.Email, Password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var token = Assert.IsType<LoginResponseDto>(await login.Content.ReadFromJsonAsync<LoginResponseDto>());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            var request = new CheckoutRequestDto
            {
                CheckoutRequestId = Guid.NewGuid(), ShippingAddressId = s.Address.Id,
                PaymentMethod = PaymentMethod.CashOnDelivery, VoucherCode = s.Voucher.Code
            };
            const string route = "/api/orders/checkout";

            async Task ExpectConflictAsync(string expected)
            {
                using var response = await client.PostAsJsonAsync(route, request);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());
                Assert.Equal(expected, problem.Detail);
            }

            // Temporary guard: valid voucher must not create a full-price order.
            await ExpectConflictAsync("CHECKOUT_VOUCHER_NOT_ENABLED");
            await AssertUnredeemedAsync(s);

            // No order was committed, so the same request ID can still be used.
            request.VoucherCode = null;
            using var create = await client.PostAsJsonAsync(route, request);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = Assert.IsType<CheckoutResultDto>(await create.Content.ReadFromJsonAsync<CheckoutResultDto>());
            Assert.False(created.IsReplay);
            Assert.Equal(500000m, created.Subtotal);
            Assert.Equal(0m, created.DiscountAmount);
            Assert.Equal(500000m, created.TotalAmount);
            var order = await s.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == created.OrderId);
            Assert.Null(order.VoucherId);
            Assert.Equal(CheckoutRequestFingerprint.Compute(request), order.CheckoutRequestFingerprint);
            Assert.Equal(500000m, await s.Db.Payments.Where(x => x.OrderId == order.Id).Select(x => x.Amount).SingleAsync());

            request.VoucherCode = " \t ";
            using var replay = await client.PostAsJsonAsync(route, request);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayed = Assert.IsType<CheckoutResultDto>(await replay.Content.ReadFromJsonAsync<CheckoutResultDto>());
            Assert.True(replayed.IsReplay);
            Assert.Equal(created.OrderId, replayed.OrderId);

            request.VoucherCode = s.Voucher.Code;
            await ExpectConflictAsync("CHECKOUT_REQUEST_MISMATCH");

            // Simulate the incomplete v2 records that the missing step-B guard could create.
            // Only the test fixture is modified; the application must not silently replay it.
            await s.Db.Orders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(p =>
                p.SetProperty(x => x.CheckoutRequestFingerprint, CheckoutRequestFingerprint.Compute(request)));
            await ExpectConflictAsync("CHECKOUT_ORDER_DATA_INCOMPLETE");
            Assert.Equal(1, await s.Db.Orders.CountAsync(x => x.UserId == s.Customer.Id));
            Assert.Equal(8, await s.Db.Products.Where(x => x.Id == s.Product.Id).Select(x => x.StockQuantity).SingleAsync());
            Assert.Equal(0, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id).Select(x => x.UsedCount).SingleAsync());
            Assert.False(await s.Db.CartItems.AnyAsync(x => x.CartId == s.Cart.Id));
        });
    }

    private static async Task<CheckoutPrice> PrepareLockedAsync(TestState s, Guid userId, string code, decimal subtotal = 500000m)
    {
        using var scope = s.Factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var pricing = scope.ServiceProvider.GetRequiredService<ICheckoutPricingService>();
        return await uow.ExecuteInLockedTransactionAsync($"flower-shop:cart:{userId:N}",
            ct => pricing.PrepareAsync(userId, code, subtotal, ct));
    }

    private static async Task ExpectBusinessAsync(string code, Func<Task<CheckoutPrice>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => { await action(); });
        Assert.Equal(code, ex.Message);
        Assert.Equal(code == "VOUCHER_NOT_FOUND" ? BusinessErrorType.NotFound : BusinessErrorType.Conflict, ex.ErrorType);
    }

    private static async Task<string> ReadModeAsync(FlowerShopDbContext db, string resource, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("TRANSACTION_REQUIRED");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT APPLOCK_MODE(N'public', @resource, N'Transaction');";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Size = 255;
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(ct));
    }

    private static async Task SetCountAsync(TestState s, int count)
    {
        Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.UsedCount, count)));
    }

    private static async Task AssertUnredeemedAsync(TestState s)
    {
        Assert.Equal(0, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id).Select(x => x.UsedCount).SingleAsync());
        Assert.Equal(10, await s.Db.Products.Where(x => x.Id == s.Product.Id).Select(x => x.StockQuantity).SingleAsync());
        Assert.Equal(2, await s.Db.CartItems.Where(x => x.CartId == s.Cart.Id).Select(x => x.Quantity).SingleAsync());
        Assert.False(await s.Db.Orders.AnyAsync(x => x.UserId == s.Customer.Id || x.UserId == s.Other.Id));
    }

    private static Order NewHistory(Guid userId, Guid voucherId, OrderStatus status) => new()
    {
        OrderCode = $"CP-{Guid.NewGuid():N}", UserId = userId, VoucherId = voucherId,
        Status = status, RecipientName = "Test Customer", RecipientPhone = "0900000000",
        ShippingAddressLine = "Test Street", ShippingWard = "Ward", ShippingDistrict = "District",
        ShippingProvince = "Da Nang", ShippingCountryCode = "VN",
        Subtotal = 500000m, DiscountAmount = 100000m, TotalAmount = 400000m
    };

    private const string Password = "FlowerShop#2026";
    private static string Connection() => Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

    private static async Task WithDatabaseAsync(Func<TestState, Task> action)
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        User NewUser()
        {
            var user = new User
            {
                FullName = "Checkout Pricing Test", Email = $"cp-{Guid.NewGuid():N}@example.invalid",
                Role = UserRole.Customer, IsActive = true
            };
            user.PasswordHash = hasher.HashPassword(user, Password);
            return user;
        }
        var customer = NewUser();
        var other = NewUser();
        var category = new Category { Name = "Checkout Pricing Test", Slug = $"cp-cat-{Guid.NewGuid():N}" };
        var product = new Product { CategoryId = category.Id, Name = "Rose", Slug = $"cp-product-{Guid.NewGuid():N}",
            Price = 250000m, StockQuantity = 10, IsActive = true };
        var cart = new Cart { UserId = customer.Id };
        var item = new CartItem { CartId = cart.Id, ProductId = product.Id, Quantity = 2 };
        var address = new UserAddress { UserId = customer.Id, RecipientName = "Customer", RecipientPhone = "0900000000",
            AddressLine = "Test Street", Ward = "Ward", District = "District", Province = "Da Nang", CountryCode = "VN", IsDefault = true };
        var voucher = new Voucher
        {
            Code = $"CP-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, MinOrderAmount = 300000m, MaxDiscountAmount = 120000m,
            UsageLimit = 10, PerUserLimit = 1, UsedCount = 0, IsActive = true,
            StartAt = DateTime.UtcNow.AddDays(-1), EndAt = DateTime.UtcNow.AddDays(1)
        };
        Guid[] userIds = [customer.Id, other.Id];
        try
        {
            db.AddRange(customer, other, category, product, cart, item, address, voucher);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await action(new TestState(factory, db, customer, other, category, product, cart, address, voucher));
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orderIds = await db.Orders.Where(x => userIds.Contains(x.UserId)).Select(x => x.Id).ToArrayAsync();
            await db.Payments.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.OrderItems.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(x => orderIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.CartItems.Where(x => x.CartId == cart.Id).ExecuteDeleteAsync();
            await db.Carts.Where(x => x.Id == cart.Id).ExecuteDeleteAsync();
            await db.UserAddresses.Where(x => x.Id == address.Id).ExecuteDeleteAsync();
            await db.Products.Where(x => x.Id == product.Id).ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id).ExecuteDeleteAsync();
            await db.Vouchers.Where(x => x.Id == voucher.Id).ExecuteDeleteAsync();
            await db.Users.Where(x => userIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed record TestState(TestApiFactory Factory, FlowerShopDbContext Db, User Customer, User Other,
        Category Category, Product Product, Cart Cart, UserAddress Address, Voucher Voucher);
}
