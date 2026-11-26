using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class VoucherCheckoutHttpTests
{
    private const string CheckoutRoute = "/api/orders/checkout";
    private const string Password = "VoucherHttp#2026";

    [Fact]
    public async Task Checkout_ShouldEnforceJwtAndUseServerPricingAndReplaySnapshots()
    {
        await WithDatabaseAsync(async s =>
        {
            var request = Request(s, s.Alice);
            using var anonymous = Client(s);
            using var admin = await LoginAsync(s, s.Admin);
            using var alice = await LoginAsync(s, s.Alice.User);
            using var bob = await LoginAsync(s, s.Bob.User);

            using (var response = await anonymous.PostAsJsonAsync(CheckoutRoute, request))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using (var response = await admin.PostAsJsonAsync(CheckoutRoute, request))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await ExpectErrorAsync(bob, request, HttpStatusCode.NotFound, "SHIPPING_ADDRESS_NOT_FOUND");
            await AssertUsageAsync(s, 0, 0);
            await AssertShopperAsync(s, s.Alice, 10, 2);
            await AssertShopperAsync(s, s.Bob, 10, 2);

            using var preview = await alice.PostAsJsonAsync("/api/vouchers/preview", new
            {
                voucherCode = s.Voucher.Code.ToLowerInvariant(), userId = s.Bob.User.Id,
                subtotal = 1m, discountAmount = 499999m
            });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var quote = Assert.IsType<VoucherPreviewResultDto>(await preview.Content.ReadFromJsonAsync<VoucherPreviewResultDto>());
            Assert.Equal(500000m, quote.Subtotal);
            Assert.Equal(100000m, quote.DiscountAmount);
            Assert.Equal(400000m, quote.TotalAmount);
            await AssertUsageAsync(s, 0, 0);
            await AssertShopperAsync(s, s.Alice, 10, 2);

            // Untrusted fields do not belong to CheckoutRequestDto and cannot override pricing or identity.
            var forged = new
            {
                request.CheckoutRequestId, request.ShippingAddressId, request.PaymentMethod, request.Note,
                voucherCode = $" {s.Voucher.Code.ToLowerInvariant()} ",
                userId = s.Bob.User.Id, voucherId = Guid.NewGuid(), subtotal = 1m,
                shippingFee = 0m, discountAmount = 499999m, totalAmount = 1m, usedCount = 99
            };
            var created = await ExpectCheckoutAsync(alice, forged, HttpStatusCode.Created, false);
            await AssertOrderAsync(s, s.Alice, request, created, 100000m);
            await AssertUsageAsync(s, 1, 1);
            await AssertShopperAsync(s, s.Alice, 8, 0);
            await AssertShopperAsync(s, s.Bob, 10, 2);

            // Change eligibility through the real Admin API, not direct SQL.
            using (var off = await admin.PatchAsJsonAsync($"/api/admin/vouchers/{s.Voucher.Id}/status",
                new UpdateVoucherStatusDto { IsActive = false }))
            {
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
                Assert.False(Assert.IsType<VoucherDto>(await off.Content.ReadFromJsonAsync<VoucherDto>()).IsActive);
            }
            request.VoucherCode = $" {s.Voucher.Code.ToLowerInvariant()} ";
            var replay = await ExpectCheckoutAsync(alice, request, HttpStatusCode.OK, true);
            Assert.Equal(created.OrderId, replay.OrderId);
            Assert.Equal(created.OrderCode, replay.OrderCode);
            Assert.Equal(created.DiscountAmount, replay.DiscountAmount);
            Assert.Equal(created.TotalAmount, replay.TotalAmount);
            Assert.Equal(created.Status, replay.Status);
            Assert.Equal(created.PaymentStatus, replay.PaymentStatus);
            await ExpectErrorAsync(bob, request, HttpStatusCode.Conflict, "CHECKOUT_REQUEST_ID_IN_USE");

            request.VoucherCode = "DIFFERENT-CODE";
            await ExpectErrorAsync(alice, request, HttpStatusCode.Conflict, "CHECKOUT_REQUEST_MISMATCH");
            request.VoucherCode = null;
            await ExpectErrorAsync(alice, request, HttpStatusCode.Conflict, "CHECKOUT_REQUEST_MISMATCH");
            await AssertUsageAsync(s, 1, 1);
            await AssertShopperAsync(s, s.Alice, 8, 0);
            await AssertShopperAsync(s, s.Bob, 10, 2);
        });
    }

    [Theory]
    [InlineData("invalid", 400, "VOUCHER_CODE_INVALID")]
    [InlineData("missing", 404, "VOUCHER_NOT_FOUND")]
    [InlineData("inactive", 409, "VOUCHER_INACTIVE")]
    [InlineData("expired", 409, "VOUCHER_EXPIRED")]
    [InlineData("upcoming", 409, "VOUCHER_NOT_STARTED")]
    [InlineData("minimum", 409, "VOUCHER_MIN_ORDER_NOT_MET")]
    [InlineData("zero-total", 409, "CHECKOUT_TOTAL_OUT_OF_RANGE")]
    public async Task Checkout_InvalidVoucher_ShouldReturnExpectedHttpErrorWithoutWrites(
        string scenario, int status, string code)
    {
        await WithDatabaseAsync(async s =>
        {
            var request = Request(s, s.Alice);
            var vouchers = s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id);
            switch (scenario)
            {
                case "invalid": request.VoucherCode = "FLOWER@20"; break;
                case "missing": request.VoucherCode = $"MISSING-{Guid.NewGuid():N}".ToUpperInvariant(); break;
                case "inactive": await vouchers.ExecuteUpdateAsync(p => p.SetProperty(x => x.IsActive, false)); break;
                case "expired": await vouchers.ExecuteUpdateAsync(p => p.SetProperty(x => x.EndAt, s.Voucher.StartAt.AddHours(1))); break;
                case "upcoming": await vouchers.ExecuteUpdateAsync(p => p
                    .SetProperty(x => x.StartAt, s.Voucher.EndAt.AddDays(1))
                    .SetProperty(x => x.EndAt, s.Voucher.EndAt.AddDays(2))); break;
                case "minimum": await vouchers.ExecuteUpdateAsync(p => p.SetProperty(x => x.MinOrderAmount, 600000m)); break;
                case "zero-total": await vouchers.ExecuteUpdateAsync(p => p.SetProperty(x => x.DiscountValue, 100m)); break;
                default: throw new InvalidOperationException(scenario);
            }
            var before = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
            using var alice = await LoginAsync(s, s.Alice.User);
            await ExpectErrorAsync(alice, request, (HttpStatusCode)status, code);
            await AssertUsageAsync(s, 0, 0);
            await AssertShopperAsync(s, s.Alice, 10, 2);
            await AssertShopperAsync(s, s.Bob, 10, 2);
            var after = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        });
    }

    [Fact]
    public async Task Checkout_NewRequest_ShouldEnforcePerUserLimit()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            var first = Request(s, s.Alice);
            var created = await ExpectCheckoutAsync(alice, first, HttpStatusCode.Created, false);
            await AssertOrderAsync(s, s.Alice, first, created, 100000m);
            s.Db.CartItems.Add(new CartItem { CartId = s.Alice.Cart.Id, ProductId = s.Alice.Product.Id, Quantity = 2 });
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
            var next = Request(s, s.Alice);
            await ExpectErrorAsync(alice, next, HttpStatusCode.Conflict, "VOUCHER_PER_USER_LIMIT_REACHED");
            await AssertUsageAsync(s, 1, 1);
            await AssertShopperAsync(s, s.Alice, 8, 2);
            Assert.False(await s.Db.Orders.AnyAsync(x => x.OrderCode == $"FS-{next.CheckoutRequestId:N}"));
        });
    }

    [Fact]
    public async Task Checkout_ConcurrentCustomersForLastUse_ShouldCommitExactlyOneOrder()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            using var bob = await LoginAsync(s, s.Bob.User);
            var ar = Request(s, s.Alice);
            var br = Request(s, s.Bob);
            // Both HTTP requests are started before awaiting results. No assumption about the winner.
            var attempts = await Task.WhenAll(AttemptAsync(alice, ar), AttemptAsync(bob, br));
            var winner = Assert.Single(attempts, x => x.Status == HttpStatusCode.Created);
            var loser = Assert.Single(attempts, x => x.Status == HttpStatusCode.Conflict);
            Assert.Equal("VOUCHER_USAGE_LIMIT_REACHED", loser.Error);
            var result = Assert.IsType<CheckoutResultDto>(winner.Result);
            Assert.False(result.IsReplay);
            var aliceWon = attempts[0].Status == HttpStatusCode.Created;
            await AssertOrderAsync(s, aliceWon ? s.Alice : s.Bob, aliceWon ? ar : br, result, 100000m);
            await AssertUsageAsync(s, 1, 1);
            await AssertShopperAsync(s, s.Alice, aliceWon ? 8 : 10, aliceWon ? 0 : 2);
            await AssertShopperAsync(s, s.Bob, aliceWon ? 10 : 8, aliceWon ? 2 : 0);
            // A separate retry with the losing request must still be rejected.
            await ExpectErrorAsync(aliceWon ? bob : alice, aliceWon ? br : ar,
                HttpStatusCode.Conflict, "VOUCHER_USAGE_LIMIT_REACHED");
            await AssertUsageAsync(s, 1, 1);
        }, limit: 1);
    }

    [Fact]
    public async Task Checkout_WithoutVoucher_ShouldPreserveV1AndRejectAddingVoucherOnReplay()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            var request = Request(s, s.Alice);
            request.VoucherCode = null;
            var created = await ExpectCheckoutAsync(alice, request, HttpStatusCode.Created, false);
            await AssertOrderAsync(s, s.Alice, request, created, 0m);
            request.VoucherCode = "   ";
            var replay = await ExpectCheckoutAsync(alice, request, HttpStatusCode.OK, true);
            Assert.Equal(created.OrderId, replay.OrderId);
            Assert.Equal(500000m, replay.TotalAmount);
            request.VoucherCode = s.Voucher.Code;
            await ExpectErrorAsync(alice, request, HttpStatusCode.Conflict, "CHECKOUT_REQUEST_MISMATCH");
            await AssertUsageAsync(s, 0, 1);
            await AssertShopperAsync(s, s.Alice, 8, 0);
        });
    }

    private static CheckoutRequestDto Request(State s, Shopper shopper) => new()
    {
        CheckoutRequestId = Guid.NewGuid(), ShippingAddressId = shopper.Address.Id,
        PaymentMethod = PaymentMethod.CashOnDelivery, VoucherCode = s.Voucher.Code
    };

    private static HttpClient Client(State s) => s.Factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task<HttpClient> LoginAsync(State s, User user)
    {
        var client = Client(s);
        try
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = user.Email, Password = Password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var login = Assert.IsType<LoginResponseDto>(await response.Content.ReadFromJsonAsync<LoginResponseDto>());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<CheckoutResultDto> ExpectCheckoutAsync<T>(HttpClient client, T request,
        HttpStatusCode status, bool replay)
    {
        using var response = await client.PostAsJsonAsync(CheckoutRoute, request);
        Assert.Equal(status, response.StatusCode);
        var result = Assert.IsType<CheckoutResultDto>(await response.Content.ReadFromJsonAsync<CheckoutResultDto>());
        Assert.Equal(replay, result.IsReplay);
        return result;
    }

    private static async Task ExpectErrorAsync(HttpClient client, CheckoutRequestDto request, HttpStatusCode status, string code)
    {
        using var response = await client.PostAsJsonAsync(CheckoutRoute, request);
        Assert.Equal(status, response.StatusCode);
        if (status == HttpStatusCode.BadRequest)
        {
            var problem = Assert.IsType<ValidationProblemDetails>(await response.Content.ReadFromJsonAsync<ValidationProblemDetails>());
            Assert.Contains(code, problem.Errors.Values.SelectMany(x => x));
        }
        else
        {
            var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());
            Assert.Equal(code, problem.Detail);
        }
    }

    private static async Task<Attempt> AttemptAsync(HttpClient client, CheckoutRequestDto request)
    {
        using var response = await client.PostAsJsonAsync(CheckoutRoute, request);
        if (response.StatusCode == HttpStatusCode.Created)
            return new(response.StatusCode, Assert.IsType<CheckoutResultDto>(await response.Content.ReadFromJsonAsync<CheckoutResultDto>()), null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());
        return new(response.StatusCode, null, problem.Detail);
    }

    private static async Task AssertUsageAsync(State s, int used, int orders)
    {
        var voucher = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
        Assert.Equal(used, voucher.UsedCount);
        Assert.Equal((long)used, await s.Db.Orders.LongCountAsync(x => x.VoucherId == voucher.Id && x.Status != OrderStatus.Cancelled));
        Assert.Equal(orders, await s.Db.Orders.CountAsync(x => x.UserId == s.Alice.User.Id || x.UserId == s.Bob.User.Id));
        Assert.True(!voucher.UsageLimit.HasValue || voucher.UsedCount <= voucher.UsageLimit.Value);
    }

    private static async Task AssertShopperAsync(State s, Shopper shopper, int stock, int quantity)
    {
        Assert.Equal(stock, await s.Db.Products.Where(x => x.Id == shopper.Product.Id).Select(x => x.StockQuantity).SingleAsync());
        Assert.Equal(quantity, await s.Db.CartItems.Where(x => x.CartId == shopper.Cart.Id).SumAsync(x => x.Quantity));
    }

    private static async Task AssertOrderAsync(State s, Shopper shopper, CheckoutRequestDto request,
        CheckoutResultDto result, decimal discount)
    {
        var order = await s.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == result.OrderId);
        Assert.Equal(shopper.User.Id, order.UserId);
        Guid? voucherId = discount == 0m ? null : s.Voucher.Id;
        Assert.Equal(voucherId, order.VoucherId);
        Assert.Equal($"FS-{request.CheckoutRequestId:N}", order.OrderCode);
        Assert.Equal(order.OrderCode, result.OrderCode);
        Assert.Equal(CheckoutRequestFingerprint.Compute(request), order.CheckoutRequestFingerprint);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(OrderStatus.Pending, result.Status);
        Assert.Equal(PaymentMethod.CashOnDelivery, result.PaymentMethod);
        Assert.Equal(PaymentStatus.Pending, result.PaymentStatus);
        Assert.Equal(500000m, order.Subtotal);
        Assert.Equal(0m, order.ShippingFee);
        Assert.Equal(discount, order.DiscountAmount);
        Assert.Equal(500000m - discount, order.TotalAmount);
        Assert.Equal(order.Subtotal, result.Subtotal);
        Assert.Equal(order.ShippingFee, result.ShippingFee);
        Assert.Equal(order.DiscountAmount, result.DiscountAmount);
        Assert.Equal(order.TotalAmount, result.TotalAmount);
        var payment = await s.Db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal(PaymentMethod.CashOnDelivery, payment.Method);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal("VND", payment.Currency);
        Assert.Equal(order.TotalAmount, payment.Amount);
        Assert.Null(payment.PaidAt);
        var item = await s.Db.OrderItems.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal(shopper.Product.Id, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(250000m, item.UnitPrice);
        Assert.Equal(500000m, item.LineTotal);
        Assert.Single(result.Items);
    }

    private static async Task WithDatabaseAsync(Func<State, Task> action, int limit = 10)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        // Exercise the actual release DI, not a test-only enabled override.
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.True(sp.GetRequiredService<CheckoutVoucherRollout>().Enabled);
        var db = sp.GetRequiredService<FlowerShopDbContext>();
        Assert.Contains("20261124130728_AddVoucherUsageCompositeIndex", await db.Database.GetAppliedMigrationsAsync());
        var hasher = sp.GetRequiredService<IPasswordHasher<User>>();
        User NewUser(UserRole role)
        {
            var user = new User { FullName = "Voucher Checkout HTTP Test", Email = $"vch-{Guid.NewGuid():N}@example.invalid", Role = role, IsActive = true };
            user.PasswordHash = hasher.HashPassword(user, Password);
            return user;
        }
        var category = new Category { Name = "Voucher HTTP Test", Slug = $"vch-cat-{Guid.NewGuid():N}" };
        Shopper NewShopper()
        {
            var user = NewUser(UserRole.Customer);
            var product = new Product { CategoryId = category.Id, Name = "Rose", Slug = $"vch-product-{Guid.NewGuid():N}", Price = 250000m, StockQuantity = 10, IsActive = true };
            var cart = new Cart { UserId = user.Id };
            var address = new UserAddress { UserId = user.Id, RecipientName = "HTTP Test Customer", RecipientPhone = "0900000000",
                AddressLine = "Test Street", Ward = "Ward", District = "District", Province = "Da Nang", CountryCode = "VN", IsDefault = true };
            return new Shopper(user, product, cart, address);
        }
        var alice = NewShopper();
        var bob = NewShopper();
        var admin = NewUser(UserRole.Admin);
        var now = DateTime.UtcNow;
        var voucher = new Voucher { Code = $"VCH-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, MinOrderAmount = 300000m, UsageLimit = limit, PerUserLimit = 1,
            UsedCount = 0, IsActive = true, StartAt = now.AddDays(-1), EndAt = now.AddDays(1) };
        Guid[] userIds = [alice.User.Id, bob.User.Id, admin.Id];
        Guid[] cartIds = [alice.Cart.Id, bob.Cart.Id];
        Guid[] productIds = [alice.Product.Id, bob.Product.Id];
        try
        {
            db.AddRange(category, admin, voucher);
            foreach (var shopper in new[] { alice, bob })
            {
                db.AddRange(shopper.User, shopper.Product, shopper.Cart, shopper.Address);
                db.CartItems.Add(new CartItem { CartId = shopper.Cart.Id, ProductId = shopper.Product.Id, Quantity = 2 });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await action(new State(factory, db, voucher, alice, bob, admin));
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orderIds = await db.Orders.Where(x => userIds.Contains(x.UserId)).Select(x => x.Id).ToArrayAsync();
            await db.Payments.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.OrderItems.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(x => orderIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.CartItems.Where(x => cartIds.Contains(x.CartId)).ExecuteDeleteAsync();
            await db.Carts.Where(x => cartIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.UserAddresses.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync();
            await db.Products.Where(x => productIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id).ExecuteDeleteAsync();
            await db.Vouchers.Where(x => x.Id == voucher.Id).ExecuteDeleteAsync();
            await db.Users.Where(x => userIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed record Attempt(HttpStatusCode Status, CheckoutResultDto? Result, string? Error);
    private sealed record Shopper(User User, Product Product, Cart Cart, UserAddress Address);
    private sealed record State(WebApplicationFactory<FlowerShop.API.Program> Factory, FlowerShopDbContext Db,
        Voucher Voucher, Shopper Alice, Shopper Bob, User Admin);
}
