using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class CrossUserCheckoutConcurrencyHttpTests
{
    [Fact]
    public async Task SameRequestIdAcrossCustomers_ShouldCreateOnlyOneOrder()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

        var sql = new SqlConnectionStringBuilder(connection);

        if (!string.Equals(
                sql.InitialCatalog,
                "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase) ||
            sql.MultipleActiveResultSets)
        {
            throw new InvalidOperationException(
                "Use FlowerShop_Test with MARS disabled.");
        }

        using var factory = new TestApiFactory(connection);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<User>>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_AddCheckoutRequestFingerprint"));

        const string password = "TestPassword#2026";
        var suffix = Guid.NewGuid().ToString("N");

        User NewCustomer(string label)
        {
            var user = new User
            {
                FullName = $"Checkout {label}",
                Email = $"cross-user-{label}-{suffix}@example.invalid",
                Role = UserRole.Customer,
                IsActive = true
            };

            user.PasswordHash =
                hasher.HashPassword(user, password);

            return user;
        }

        UserAddress NewAddress(User user, string label) => new()
        {
            UserId = user.Id,
            RecipientName = label,
            RecipientPhone = "0900000000",
            AddressLine = $"{label} Flower Street",
            Ward = "Test Ward",
            District = "Test District",
            Province = "Da Nang",
            CountryCode = "VN"
        };

        var alice = NewCustomer("Alice");
        var bob = NewCustomer("Bob");

        var aliceAddress = NewAddress(alice, "Alice");
        var bobAddress = NewAddress(bob, "Bob");

        var category = new Category
        {
            Name = "Cross User Checkout",
            Slug = $"cross-user-category-{suffix}"
        };

        var rose = new Product
        {
            Name = "Race Test Rose",
            Slug = $"cross-user-rose-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 5,
            IsActive = true
        };

        var tulip = new Product
        {
            Name = "Race Test Tulip",
            Slug = $"cross-user-tulip-{suffix}",
            CategoryId = category.Id,
            Price = 80000m,
            StockQuantity = 5,
            IsActive = true
        };

        var aliceCart = new Cart { UserId = alice.Id };
        var bobCart = new Cart { UserId = bob.Id };

        var aliceItem = new CartItem
        {
            CartId = aliceCart.Id,
            ProductId = rose.Id,
            Quantity = 2
        };

        var bobItem = new CartItem
        {
            CartId = bobCart.Id,
            ProductId = tulip.Id,
            Quantity = 3
        };

        db.AddRange(
            alice, bob,
            aliceAddress, bobAddress,
            category, rose, tulip,
            aliceCart, bobCart,
            aliceItem, bobItem);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        async Task<string> LoginAsync(User user)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginDto
                {
                    Email = user.Email,
                    Password = password
                });

            Assert.Equal(
                HttpStatusCode.OK,
                response.StatusCode);

            var login = await response.Content
                .ReadFromJsonAsync<LoginResponseDto>();

            return Assert.IsType<LoginResponseDto>(
                login).AccessToken;
        }

        async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string url,
            string token,
            object? body = null)
        {
            using var request = new HttpRequestMessage(
                method, url);

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", token);

            if (body is not null)
                request.Content = JsonContent.Create(body);

            return await client.SendAsync(request);
        }

        Task<HttpResponseMessage> CheckoutAsync(
            CheckoutRequestDto dto,
            string token) =>
            SendAsync(
                HttpMethod.Post,
                "/api/orders/checkout",
                token,
                dto);

        Task<int> StockAsync(Guid productId) =>
            db.Products.AsNoTracking()
                .Where(x => x.Id == productId)
                .Select(x => x.StockQuantity)
                .SingleAsync();

        Task<int> CartItemCountAsync(Guid cartId) =>
            db.CartItems.AsNoTracking()
                .CountAsync(x => x.CartId == cartId);

        try
        {
            var aliceToken = await LoginAsync(alice);
            var bobToken = await LoginAsync(bob);

            var sharedRequestId = Guid.NewGuid();
            var sharedOrderCode =
                $"FS-{sharedRequestId:N}";

            var aliceRequest = new CheckoutRequestDto
            {
                CheckoutRequestId = sharedRequestId,
                ShippingAddressId = aliceAddress.Id,
                PaymentMethod = PaymentMethod.CashOnDelivery
            };

            var bobRequest = new CheckoutRequestDto
            {
                CheckoutRequestId = sharedRequestId,
                ShippingAddressId = bobAddress.Id,
                PaymentMethod = PaymentMethod.CashOnDelivery
            };

            // SCENARIO 1:
            // Two different Customers simultaneously use
            // the same globally unique CheckoutRequestId.
            var responses = await Task.WhenAll(
                CheckoutAsync(aliceRequest, aliceToken),
                CheckoutAsync(bobRequest, bobToken));

            try
            {
                var success = Assert.Single(
                    responses,
                    x => x.StatusCode == HttpStatusCode.Created);

                var conflict = Assert.Single(
                    responses,
                    x => x.StatusCode == HttpStatusCode.Conflict);

                var createdBody = await success.Content
                    .ReadFromJsonAsync<CheckoutResultDto>();

                var created = Assert.IsType<
                    CheckoutResultDto>(createdBody);

                Assert.False(created.IsReplay);
                Assert.Equal(sharedOrderCode, created.OrderCode);

                var problemBody = await conflict.Content
                    .ReadFromJsonAsync<ProblemDetails>();

                var problem =
                    Assert.IsType<ProblemDetails>(problemBody);

                // Both outcomes are valid:
                // 1. Conflict detected after the first commit.
                // 2. SQL unique index rejects a concurrent INSERT.
                Assert.True(
                    problem.Detail is
                        "CHECKOUT_REQUEST_ID_IN_USE" or
                        "UNIQUE_CONSTRAINT_VIOLATION",
                    $"Unexpected error: {problem.Detail}");

                var aliceWon =
                    responses[0].StatusCode ==
                    HttpStatusCode.Created;

                var winner = aliceWon ? alice : bob;

                var winnerToken =
                    aliceWon ? aliceToken : bobToken;

                var loserToken =
                    aliceWon ? bobToken : aliceToken;

                var winnerRequest =
                    aliceWon ? aliceRequest : bobRequest;

                var loserRequest =
                    aliceWon ? bobRequest : aliceRequest;

                var winnerProduct =
                    aliceWon ? rose : tulip;

                var loserProduct =
                    aliceWon ? tulip : rose;

                var winnerCart =
                    aliceWon ? aliceCart : bobCart;

                var loserCart =
                    aliceWon ? bobCart : aliceCart;

                var winnerQuantity = aliceWon ? 2 : 3;
                var loserQuantity = aliceWon ? 3 : 2;

                var expectedTotal =
                    winnerProduct.Price * winnerQuantity;

                // SCENARIO 2:
                // Only one Order can own the shared code.
                Assert.Equal(
                    1,
                    await db.Orders.AsNoTracking()
                        .CountAsync(x =>
                            x.OrderCode == sharedOrderCode));

                var persistedOrder =
                    await db.Orders.AsNoTracking()
                        .SingleAsync(x =>
                            x.OrderCode == sharedOrderCode);

                Assert.Equal(winner.Id, persistedOrder.UserId);
                Assert.Equal(created.OrderId, persistedOrder.Id);
                Assert.Equal(expectedTotal, persistedOrder.Subtotal);
                Assert.Equal(expectedTotal, persistedOrder.TotalAmount);
                Assert.Equal(OrderStatus.Pending, persistedOrder.Status);

                var payment = await db.Payments.AsNoTracking()
                    .SingleAsync(x =>
                        x.OrderId == persistedOrder.Id);

                Assert.Equal(
                    PaymentMethod.CashOnDelivery,
                    payment.Method);

                Assert.Equal(PaymentStatus.Pending, payment.Status);
                Assert.Equal(expectedTotal, payment.Amount);
                Assert.Equal("VND", payment.Currency);
                Assert.Null(payment.PaidAt);

                var orderItem = await db.OrderItems.AsNoTracking()
                    .SingleAsync(x =>
                        x.OrderId == persistedOrder.Id);

                Assert.Equal(winnerProduct.Id, orderItem.ProductId);
                Assert.Equal(winnerQuantity, orderItem.Quantity);
                Assert.Equal(winnerProduct.Price, orderItem.UnitPrice);

                // SCENARIO 3:
                // The losing transaction must not consume stock
                // or remove its Customer's CartItem.
                Assert.Equal(
                    5 - winnerQuantity,
                    await StockAsync(winnerProduct.Id));

                Assert.Equal(
                    5,
                    await StockAsync(loserProduct.Id));

                Assert.Equal(
                    0,
                    await CartItemCountAsync(winnerCart.Id));

                Assert.Equal(
                    1,
                    await CartItemCountAsync(loserCart.Id));

                // SCENARIO 4:
                // Loser cannot read the winning Customer's Order.
                using (var hidden = await SendAsync(
                    HttpMethod.Get,
                    $"/api/orders/{created.OrderId}",
                    loserToken))
                {
                    Assert.Equal(
                        HttpStatusCode.NotFound,
                        hidden.StatusCode);
                }

                // A retry by the winner is a valid replay.
                using (var replayResponse =
                    await CheckoutAsync(
                        winnerRequest, winnerToken))
                {
                    Assert.Equal(
                        HttpStatusCode.OK,
                        replayResponse.StatusCode);

                    var replayBody = await replayResponse.Content
                        .ReadFromJsonAsync<CheckoutResultDto>();

                    var replay =
                        Assert.IsType<CheckoutResultDto>(replayBody);

                    Assert.True(replay.IsReplay);
                    Assert.Equal(created.OrderId, replay.OrderId);
                    Assert.Equal(expectedTotal, replay.TotalAmount);
                }

                // The loser cannot reuse the same RequestId.
                using (var loserReplay = await CheckoutAsync(
                    loserRequest, loserToken))
                {
                    Assert.Equal(
                        HttpStatusCode.Conflict,
                        loserReplay.StatusCode);

                    var retryProblem = await loserReplay.Content
                        .ReadFromJsonAsync<ProblemDetails>();

                    Assert.Equal(
                        "CHECKOUT_REQUEST_ID_IN_USE",
                        Assert.IsType<ProblemDetails>(
                            retryProblem).Detail);
                }

                // SCENARIO 5:
                // Loser can successfully checkout with a NEW
                // RequestId using the preserved cart.
                var newRequest = new CheckoutRequestDto
                {
                    CheckoutRequestId = Guid.NewGuid(),
                    ShippingAddressId =
                        loserRequest.ShippingAddressId,
                    PaymentMethod = PaymentMethod.CashOnDelivery
                };

                using (var newCheckout =
                    await CheckoutAsync(newRequest, loserToken))
                {
                    Assert.Equal(
                        HttpStatusCode.Created,
                        newCheckout.StatusCode);

                    var newBody = await newCheckout.Content
                        .ReadFromJsonAsync<CheckoutResultDto>();

                    var newOrder =
                        Assert.IsType<CheckoutResultDto>(newBody);

                    Assert.False(newOrder.IsReplay);
                    Assert.NotEqual(created.OrderId, newOrder.OrderId);
                }

                Assert.Equal(
                    2,
                    await db.Orders.AsNoTracking()
                        .CountAsync(x =>
                            x.UserId == alice.Id ||
                            x.UserId == bob.Id));

                Assert.Equal(
                    5 - winnerQuantity,
                    await StockAsync(winnerProduct.Id));

                Assert.Equal(
                    5 - loserQuantity,
                    await StockAsync(loserProduct.Id));

                Assert.Equal(
                    0,
                    await CartItemCountAsync(loserCart.Id));
            }
            finally
            {
                foreach (var response in responses)
                    response.Dispose();
            }
        }
        finally
        {
            // Clean up test-owned records in FK order.
            db.ChangeTracker.Clear();

            var orderIds = await db.Orders.AsNoTracking()
                .Where(x =>
                    x.UserId == alice.Id ||
                    x.UserId == bob.Id)
                .Select(x => x.Id)
                .ToArrayAsync();

            await db.Payments
                .Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();

            await db.OrderItems
                .Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();

            await db.Orders
                .Where(x => orderIds.Contains(x.Id))
                .ExecuteDeleteAsync();

            await db.CartItems
                .Where(x =>
                    x.CartId == aliceCart.Id ||
                    x.CartId == bobCart.Id)
                .ExecuteDeleteAsync();

            await db.Carts
                .Where(x =>
                    x.Id == aliceCart.Id ||
                    x.Id == bobCart.Id)
                .ExecuteDeleteAsync();

            await db.UserAddresses
                .Where(x =>
                    x.Id == aliceAddress.Id ||
                    x.Id == bobAddress.Id)
                .ExecuteDeleteAsync();

            await db.Products
                .Where(x =>
                    x.Id == rose.Id ||
                    x.Id == tulip.Id)
                .ExecuteDeleteAsync();

            await db.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x =>
                    x.Id == alice.Id ||
                    x.Id == bob.Id)
                .ExecuteDeleteAsync();
        }
    }
}
