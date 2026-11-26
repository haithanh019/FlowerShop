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
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class VoucherCancellationHttpTests
{
    private const string CheckoutRoute = "/api/orders/checkout";
    private const string Password = "VoucherHttp#2026";

    [Fact]
    public async Task Cancel_CustomerOwnershipAndAuthorization_ShouldReleaseOnceAndAllowReuse()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            using var bob = await LoginAsync(s, s.Bob.User);
            using var admin = await LoginAsync(s, s.Admin);
            using var anonymous = Client(s);
            var request = Request(s, s.Alice);
            var created = await ExpectCheckoutAsync(alice, request, HttpStatusCode.Created, false);
            var before = await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Pending, PaymentStatus.Pending);
            var customerUrl = CancelRoute(created.OrderId);
            var adminUrl = CancelRoute(created.OrderId, admin: true);
            using (var response = await anonymous.PostAsync(customerUrl, null))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using (var response = await anonymous.PostAsync(adminUrl, null))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using (var response = await admin.PostAsync(customerUrl, null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await alice.PostAsync(adminUrl, null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await ExpectCancelErrorAsync(bob, customerUrl, HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
            await ExpectCancelErrorAsync(alice, CancelRoute(Guid.NewGuid()), HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
            await ExpectCancelErrorAsync(alice, CancelRoute(Guid.Empty), HttpStatusCode.BadRequest, "ORDER_ID_REQUIRED");
            await AssertUsageAsync(s, 1, 1);
            await AssertShopperAsync(s, s.Alice, 8, 0);
            Assert.Equal(before.UpdatedAt, (await ReadOrderAsync(s, created.OrderId)).UpdatedAt);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Pending, PaymentStatus.Pending);

            // Refill the cart independently: a new purchase is not an automatic cancellation side effect.
            await RefillAsync(s, s.Alice);
            await ExpectPreviewErrorAsync(alice, s.Voucher.Code, "VOUCHER_PER_USER_LIMIT_REACHED");
            await ExpectCancelAsync(alice, customerUrl, created, OrderStatus.Pending);
            await AssertUsageAsync(s, 0, 1);
            await AssertShopperAsync(s, s.Alice, 10, 2);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Cancelled, PaymentStatus.Cancelled);
            var restored = await ReadVoucherAsync(s);
            Assert.NotNull(restored.UpdatedAt);
            await ExpectCancelErrorAsync(alice, customerUrl, HttpStatusCode.Conflict, "ORDER_ALREADY_CANCELLED");
            await ExpectCancelErrorAsync(admin, adminUrl, HttpStatusCode.Conflict, "ORDER_ALREADY_CANCELLED");
            Assert.Equal(restored.UpdatedAt, (await ReadVoucherAsync(s)).UpdatedAt);

            request.VoucherCode = $" {s.Voucher.Code.ToLowerInvariant()} ";
            var replay = await ExpectCheckoutAsync(alice, request, HttpStatusCode.OK, true);
            Assert.Equal(created.OrderId, replay.OrderId);
            Assert.Equal(OrderStatus.Cancelled, replay.Status);
            Assert.Equal(PaymentStatus.Cancelled, replay.PaymentStatus);
            Assert.Equal(created.TotalAmount, replay.TotalAmount);
            Assert.Equal(created.DiscountAmount, replay.DiscountAmount);
            await AssertUsageAsync(s, 0, 1);
            await AssertShopperAsync(s, s.Alice, 10, 2);

            using (var preview = await alice.PostAsJsonAsync("/api/vouchers/preview", new VoucherPreviewRequestDto { VoucherCode = s.Voucher.Code }))
            {
                Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
                var quote = Assert.IsType<VoucherPreviewResultDto>(await preview.Content.ReadFromJsonAsync<VoucherPreviewResultDto>());
                Assert.Equal(400000m, quote.TotalAmount);
            }
            await AssertUsageAsync(s, 0, 1);
            var next = Request(s, s.Alice);
            var newOrder = await ExpectCheckoutAsync(alice, next, HttpStatusCode.Created, false);
            Assert.NotEqual(created.OrderId, newOrder.OrderId);
            await AssertOrderAsync(s, s.Alice, next, newOrder, OrderStatus.Pending, PaymentStatus.Pending);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Cancelled, PaymentStatus.Cancelled);
            await AssertUsageAsync(s, 1, 2);
            await AssertShopperAsync(s, s.Alice, 8, 0);
            await AssertShopperAsync(s, s.Bob, 10, 2);
        });
    }

    [Theory]
    [InlineData(true)] // Admin cancels Confirmed with inactive voucher.
    [InlineData(false)] // Customer cancels Pending with expired voucher.
    public async Task Cancel_ShouldReleaseEvenWhenVoucherIsUnavailable(bool inactive)
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            using var admin = await LoginAsync(s, s.Admin);
            var request = Request(s, s.Alice);
            var created = await ExpectCheckoutAsync(alice, request, HttpStatusCode.Created, false);
            var previous = OrderStatus.Pending;
            if (inactive)
            {
                using var off = await admin.PatchAsJsonAsync($"/api/admin/vouchers/{s.Voucher.Id}/status",
                    new UpdateVoucherStatusDto { IsActive = false });
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
                Assert.False(Assert.IsType<VoucherDto>(await off.Content.ReadFromJsonAsync<VoucherDto>()).IsActive);
                await SetStatusAsync(admin, created.OrderId, OrderStatus.Confirmed);
                previous = OrderStatus.Confirmed;
                await ExpectCancelErrorAsync(alice, CancelRoute(created.OrderId), HttpStatusCode.Conflict, "INVALID_ORDER_STATUS_TRANSITION");
            }
            else
            {
                // Move the expiry boundary into the past using a valid date range. Test setup only.
                Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                    .ExecuteUpdateAsync(p => p.SetProperty(x => x.EndAt, s.Voucher.StartAt.AddHours(1))));
            }
            // Generic status API must never bypass the restoration workflow.
            using (var direct = await admin.PatchAsJsonAsync($"/api/admin/orders/{created.OrderId}/status",
                new UpdateOrderStatusRequestDto { Status = OrderStatus.Cancelled }))
                await AssertProblemAsync(direct, HttpStatusCode.Conflict, "USE_ORDER_CANCELLATION");
            await AssertUsageAsync(s, 1, 1);
            var before = await ReadVoucherAsync(s);
            await ExpectCancelAsync(inactive ? admin : alice, CancelRoute(created.OrderId, inactive), created, previous);
            var after = await ReadVoucherAsync(s);
            Assert.Equal(before.IsActive, after.IsActive);
            Assert.Equal(before.EndAt, after.EndAt);
            Assert.Equal(before.DiscountValue, after.DiscountValue);
            await AssertUsageAsync(s, 0, 1);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Cancelled, PaymentStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
            await RefillAsync(s, s.Alice);
            await ExpectPreviewErrorAsync(alice, s.Voucher.Code, inactive ? "VOUCHER_INACTIVE" : "VOUCHER_EXPIRED");
            await AssertUsageAsync(s, 0, 1);
        }, limit: 1);
    }

    [Theory]
    [InlineData(false)] // Customer vs Customer.
    [InlineData(true)]  // Customer vs Admin.
    public async Task Cancel_ConcurrentRequests_ShouldReturnOneSuccessAndRestoreOnce(bool secondIsAdmin)
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            using var other = await LoginAsync(s, secondIsAdmin ? s.Admin : s.Alice.User);
            var request = Request(s, s.Alice);
            var created = await ExpectCheckoutAsync(alice, request, HttpStatusCode.Created, false);
            var attempts = await Task.WhenAll(
                CancelAttemptAsync(alice, CancelRoute(created.OrderId)),
                CancelAttemptAsync(other, CancelRoute(created.OrderId, secondIsAdmin)));
            var winner = Assert.Single(attempts, x => x.Status == HttpStatusCode.OK);
            var loser = Assert.Single(attempts, x => x.Status == HttpStatusCode.Conflict);
            AssertCancelResult(Assert.IsType<OrderStatusUpdateResultDto>(winner.Result), created, OrderStatus.Pending);
            Assert.Equal("ORDER_ALREADY_CANCELLED", loser.Error);
            await AssertUsageAsync(s, 0, 1);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Cancelled, PaymentStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
            await AssertShopperAsync(s, s.Bob, 10, 2);
        }, limit: 1);
    }

    [Fact]
    public async Task Cancel_AndCheckoutLastUse_ShouldPreserveCommittedUsage()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            using var bob = await LoginAsync(s, s.Bob.User);
            var ar = Request(s, s.Alice);
            var a = await ExpectCheckoutAsync(alice, ar, HttpStatusCode.Created, false);
            var br = Request(s, s.Bob);
            var cancellation = CancelAttemptAsync(alice, CancelRoute(a.OrderId));
            var checkout = CheckoutAttemptAsync(bob, br);
            await Task.WhenAll(cancellation, checkout);
            var cancelled = await cancellation;
            Assert.Equal(HttpStatusCode.OK, cancelled.Status);
            AssertCancelResult(Assert.IsType<OrderStatusUpdateResultDto>(cancelled.Result), a, OrderStatus.Pending);
            await AssertOrderAsync(s, s.Alice, ar, a, OrderStatus.Cancelled, PaymentStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
            var attempt = await checkout;
            CheckoutResultDto b;
            if (attempt.Status == HttpStatusCode.Created)
                b = Assert.IsType<CheckoutResultDto>(attempt.Result);
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, attempt.Status);
                Assert.Equal("VOUCHER_USAGE_LIMIT_REACHED", attempt.Error);
                await AssertUsageAsync(s, 0, 1);
                await AssertShopperAsync(s, s.Bob, 10, 2);
                b = await ExpectCheckoutAsync(bob, br, HttpStatusCode.Created, false);
            }
            Assert.False(b.IsReplay);
            await AssertOrderAsync(s, s.Bob, br, b, OrderStatus.Pending, PaymentStatus.Pending);
            await AssertUsageAsync(s, 1, 2);
            await AssertShopperAsync(s, s.Bob, 8, 0);
        }, limit: 1);
    }

    [Fact]
    public async Task Cancel_StockOverflow_ShouldRollbackAndAllowRetry()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            var request = Request(s, s.Alice);
            var created = await ExpectCheckoutAsync(alice, request, HttpStatusCode.Created, false);
            var before = await ReadVoucherAsync(s);
            var orderBefore = await ReadOrderAsync(s, created.OrderId);
            var paymentBefore = await ReadPaymentAsync(s, created.OrderId);
            // Real repository conditional update will reject overflow; no mock or injected exception.
            Assert.Equal(1, await s.Db.Products.Where(x => x.Id == s.Alice.Product.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.StockQuantity, int.MaxValue)));
            await ExpectCancelErrorAsync(alice, CancelRoute(created.OrderId), HttpStatusCode.Conflict, "STOCK_RESTORATION_FAILED");
            await AssertUsageAsync(s, 1, 1);
            Assert.Equal(before.UpdatedAt, (await ReadVoucherAsync(s)).UpdatedAt);
            Assert.Equal(orderBefore.UpdatedAt, (await ReadOrderAsync(s, created.OrderId)).UpdatedAt);
            Assert.Equal(paymentBefore.UpdatedAt, (await ReadPaymentAsync(s, created.OrderId)).UpdatedAt);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Pending, PaymentStatus.Pending);
            await AssertShopperAsync(s, s.Alice, int.MaxValue, 0);
            Assert.Equal(1, await s.Db.Products.Where(x => x.Id == s.Alice.Product.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.StockQuantity, 8)));
            await ExpectCancelAsync(alice, CancelRoute(created.OrderId), created, OrderStatus.Pending);
            await AssertUsageAsync(s, 0, 1);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Cancelled, PaymentStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
        });
    }

    [Fact]
    public async Task Cancel_InconsistentCounter_ShouldReturnConflictWithoutWrites()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            var request = Request(s, s.Alice);
            var created = await ExpectCheckoutAsync(alice, request, HttpStatusCode.Created, false);
            // Intentional legacy inconsistency; never a permitted application write.
            Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.UsedCount, 0)));
            var before = await ReadVoucherAsync(s);
            var orderBefore = await ReadOrderAsync(s, created.OrderId);
            var paymentBefore = await ReadPaymentAsync(s, created.OrderId);
            await ExpectCancelErrorAsync(alice, CancelRoute(created.OrderId), HttpStatusCode.Conflict, "VOUCHER_USAGE_INCONSISTENT");
            var after = await ReadVoucherAsync(s);
            Assert.Equal(0, after.UsedCount);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Equal(1L, await s.Db.Orders.LongCountAsync(x => x.VoucherId == s.Voucher.Id && x.Status != OrderStatus.Cancelled));
            Assert.Equal(orderBefore.UpdatedAt, (await ReadOrderAsync(s, created.OrderId)).UpdatedAt);
            Assert.Equal(paymentBefore.UpdatedAt, (await ReadPaymentAsync(s, created.OrderId)).UpdatedAt);
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Pending, PaymentStatus.Pending);
            await AssertShopperAsync(s, s.Alice, 8, 0);
        });
    }

    [Fact]
    public async Task Cancel_DeliveredAndPaidOrder_ShouldNeverReleaseUsage()
    {
        await WithDatabaseAsync(async s =>
        {
            using var alice = await LoginAsync(s, s.Alice.User);
            using var admin = await LoginAsync(s, s.Admin);
            var request = Request(s, s.Alice);
            var created = await ExpectCheckoutAsync(alice, request, HttpStatusCode.Created, false);
            foreach (var status in new[] { OrderStatus.Confirmed, OrderStatus.Preparing, OrderStatus.Shipping })
                await SetStatusAsync(admin, created.OrderId, status);
            using (var settled = await admin.PostAsync($"/api/admin/orders/{created.OrderId}/payments/cod/confirm", null))
                Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
            await SetStatusAsync(admin, created.OrderId, OrderStatus.Delivered);
            var before = await ReadVoucherAsync(s);
            await ExpectCancelErrorAsync(admin, CancelRoute(created.OrderId, true), HttpStatusCode.Conflict, "INVALID_ORDER_STATUS_TRANSITION");
            await ExpectCancelErrorAsync(alice, CancelRoute(created.OrderId), HttpStatusCode.Conflict, "INVALID_ORDER_STATUS_TRANSITION");
            await AssertOrderAsync(s, s.Alice, request, created, OrderStatus.Delivered, PaymentStatus.Paid);
            Assert.Equal(before.UpdatedAt, (await ReadVoucherAsync(s)).UpdatedAt);
            await AssertUsageAsync(s, 1, 1);
            await AssertShopperAsync(s, s.Alice, 8, 0);
        }, limit: 1);
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

    private static async Task<CheckoutAttempt> CheckoutAttemptAsync(HttpClient client, CheckoutRequestDto request)
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

    private static string CancelRoute(Guid orderId, bool admin = false) =>
        $"{(admin ? "/api/admin/orders" : "/api/orders")}/{orderId}/cancel";

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());
        Assert.Equal((int)status, problem.Status);
        Assert.Equal(code, problem.Detail);
    }

    private static async Task ExpectCancelErrorAsync(HttpClient client, string url, HttpStatusCode status, string code)
    {
        using var response = await client.PostAsync(url, null);
        await AssertProblemAsync(response, status, code);
    }

    private static async Task ExpectPreviewErrorAsync(HttpClient client, string code, string error)
    {
        using var response = await client.PostAsJsonAsync("/api/vouchers/preview", new VoucherPreviewRequestDto { VoucherCode = code });
        await AssertProblemAsync(response, HttpStatusCode.Conflict, error);
    }

    private static void AssertCancelResult(OrderStatusUpdateResultDto result, CheckoutResultDto created, OrderStatus previous)
    {
        Assert.Equal(created.OrderId, result.OrderId);
        Assert.Equal(created.OrderCode, result.OrderCode);
        Assert.Equal(previous, result.PreviousStatus);
        Assert.Equal(OrderStatus.Cancelled, result.Status);
    }

    private static async Task ExpectCancelAsync(HttpClient client, string url, CheckoutResultDto created, OrderStatus previous)
    {
        using var response = await client.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCancelResult(Assert.IsType<OrderStatusUpdateResultDto>(await response.Content.ReadFromJsonAsync<OrderStatusUpdateResultDto>()), created, previous);
    }

    private static async Task SetStatusAsync(HttpClient admin, Guid orderId, OrderStatus status)
    {
        using var response = await admin.PatchAsJsonAsync($"/api/admin/orders/{orderId}/status", new UpdateOrderStatusRequestDto { Status = status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.IsType<OrderStatusUpdateResultDto>(await response.Content.ReadFromJsonAsync<OrderStatusUpdateResultDto>());
        Assert.Equal(orderId, result.OrderId);
        Assert.Equal(status, result.Status);
    }

    private static async Task<CancelAttempt> CancelAttemptAsync(HttpClient client, string url)
    {
        using var response = await client.PostAsync(url, null);
        if (response.StatusCode == HttpStatusCode.OK)
            return new(response.StatusCode, Assert.IsType<OrderStatusUpdateResultDto>(await response.Content.ReadFromJsonAsync<OrderStatusUpdateResultDto>()), null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());
        return new(response.StatusCode, null, problem.Detail);
    }

    private static async Task RefillAsync(State s, Shopper shopper)
    {
        s.Db.CartItems.Add(new CartItem { CartId = shopper.Cart.Id, ProductId = shopper.Product.Id, Quantity = 2 });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }

    private static Task<Voucher> ReadVoucherAsync(State s) => s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
    private static Task<Order> ReadOrderAsync(State s, Guid id) => s.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
    private static Task<Payment> ReadPaymentAsync(State s, Guid id) => s.Db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == id);

    private static async Task<Order> AssertOrderAsync(State s, Shopper shopper, CheckoutRequestDto request,
        CheckoutResultDto created, OrderStatus status, PaymentStatus paymentStatus)
    {
        var order = await ReadOrderAsync(s, created.OrderId);
        Assert.Equal(shopper.User.Id, order.UserId);
        Assert.Equal(s.Voucher.Id, order.VoucherId);
        Assert.Equal($"FS-{request.CheckoutRequestId:N}", order.OrderCode);
        Assert.Equal(created.OrderCode, order.OrderCode);
        Assert.Equal(CheckoutRequestFingerprint.Compute(request), order.CheckoutRequestFingerprint);
        Assert.Equal(status, order.Status);
        Assert.Equal(500000m, order.Subtotal);
        Assert.Equal(0m, order.ShippingFee);
        Assert.Equal(100000m, order.DiscountAmount);
        Assert.Equal(400000m, order.TotalAmount);
        Assert.Equal(order.Subtotal, created.Subtotal);
        Assert.Equal(order.ShippingFee, created.ShippingFee);
        Assert.Equal(order.DiscountAmount, created.DiscountAmount);
        Assert.Equal(order.TotalAmount, created.TotalAmount);
        var payment = await ReadPaymentAsync(s, order.Id);
        Assert.Equal(PaymentMethod.CashOnDelivery, payment.Method);
        Assert.Equal(paymentStatus, payment.Status);
        Assert.Equal("VND", payment.Currency);
        Assert.Equal(order.TotalAmount, payment.Amount);
        if (paymentStatus == PaymentStatus.Paid) Assert.NotNull(payment.PaidAt);
        else Assert.Null(payment.PaidAt);
        var item = await s.Db.OrderItems.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal(shopper.Product.Id, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(250000m, item.UnitPrice);
        Assert.Equal(500000m, item.LineTotal);
        Assert.Single(created.Items);
        return order;
    }

    private static async Task WithDatabaseAsync(Func<State, Task> action, int limit = 10)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        using var root = new TestApiFactory(connection);
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Override only the rollout value; real authentication and all persistence remain in place.
            services.RemoveAll<CheckoutVoucherRollout>();
            services.AddSingleton(new CheckoutVoucherRollout(true));
        }));
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.True(sp.GetRequiredService<CheckoutVoucherRollout>().Enabled);
        var db = sp.GetRequiredService<FlowerShopDbContext>();
        Assert.Contains("20261124130728_AddVoucherUsageCompositeIndex", await db.Database.GetAppliedMigrationsAsync());
        var hasher = sp.GetRequiredService<IPasswordHasher<User>>();
        User NewUser(UserRole role)
        {
            var user = new User { FullName = "Voucher Cancellation HTTP Test", Email = $"vca-{Guid.NewGuid():N}@example.invalid", Role = role, IsActive = true };
            user.PasswordHash = hasher.HashPassword(user, Password);
            return user;
        }
        var category = new Category { Name = "Voucher HTTP Test", Slug = $"vca-cat-{Guid.NewGuid():N}" };
        Shopper NewShopper()
        {
            var user = NewUser(UserRole.Customer);
            var product = new Product { CategoryId = category.Id, Name = "Rose", Slug = $"vca-product-{Guid.NewGuid():N}", Price = 250000m, StockQuantity = 10, IsActive = true };
            var cart = new Cart { UserId = user.Id };
            var address = new UserAddress { UserId = user.Id, RecipientName = "HTTP Test Customer", RecipientPhone = "0900000000",
                AddressLine = "Test Street", Ward = "Ward", District = "District", Province = "Da Nang", CountryCode = "VN", IsDefault = true };
            return new Shopper(user, product, cart, address);
        }
        var alice = NewShopper();
        var bob = NewShopper();
        var admin = NewUser(UserRole.Admin);
        var now = DateTime.UtcNow;
        var voucher = new Voucher { Code = $"VCA-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
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

    private sealed record CancelAttempt(HttpStatusCode Status, OrderStatusUpdateResultDto? Result, string? Error);
    private sealed record CheckoutAttempt(HttpStatusCode Status, CheckoutResultDto? Result, string? Error);
    private sealed record Shopper(User User, Product Product, Cart Cart, UserAddress Address);
    private sealed record State(WebApplicationFactory<FlowerShop.API.Program> Factory, FlowerShopDbContext Db,
        Voucher Voucher, Shopper Alice, Shopper Bob, User Admin);
}
