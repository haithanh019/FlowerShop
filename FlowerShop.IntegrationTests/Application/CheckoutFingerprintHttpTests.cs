using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Application.Common.Idempotency;
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

public sealed class CheckoutFingerprintHttpTests
{
    [Fact]
    public async Task CheckoutFingerprint_ShouldRejectMismatchAndPreserveCommittedOrder()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(
                sql.InitialCatalog,
                "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase)
            || sql.MultipleActiveResultSets)
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

        // This test uses the new nullable database column.
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(),
            migration => migration.EndsWith("_AddCheckoutRequestFingerprint"));

        const string password = "TestPassword#2026";
        var suffix = Guid.NewGuid().ToString("N");

        User NewUser(string name)
        {
            var user = new User
            {
                FullName = $"Fingerprint {name}",
                Email = $"fingerprint-{name.ToLowerInvariant()}-{suffix}@example.invalid",
                Role = UserRole.Customer,
                IsActive = true
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var alice = NewUser("Alice");
        var bob = NewUser("Bob");
        var category = new Category
        {
            Name = "Fingerprint HTTP Test Category",
            Slug = $"fingerprint-category-{suffix}"
        };
        var rose = new Product
        {
            Name = "Original Rose Name",
            Slug = $"fingerprint-rose-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 8,
            IsActive = true
        };

        UserAddress Address(string label) => new()
        {
            UserId = alice.Id,
            RecipientName = $"Recipient {label}",
            RecipientPhone = "0900000000",
            AddressLine = $"{label} Flower Street",
            Ward = "Ward A",
            District = "District A",
            Province = "Da Nang",
            CountryCode = "VN"
        };

        var addressA = Address("One");
        var addressB = Address("Two");
        var cart = new Cart { UserId = alice.Id };
        var initialCartItem = new CartItem
        {
            CartId = cart.Id,
            ProductId = rose.Id,
            Quantity = 2
        };

        // Simulate an Order that predates the fingerprint migration.
        var legacyRequestId = Guid.NewGuid();
        var legacyOrder = new Order
        {
            UserId = alice.Id,
            OrderCode = $"FS-{legacyRequestId:N}",
            CheckoutRequestFingerprint = null,
            Status = OrderStatus.Pending,
            RecipientName = "Legacy Recipient",
            RecipientPhone = "0900000000",
            ShippingAddressLine = "Old Flower Street",
            ShippingWard = "Ward A",
            ShippingDistrict = "District A",
            ShippingProvince = "Da Nang",
            ShippingCountryCode = "VN",
            Subtotal = 100000m,
            ShippingFee = 0m,
            DiscountAmount = 0m,
            TotalAmount = 100000m,
            Items =
            [
                new OrderItem
                {
                    ProductId = rose.Id,
                    ProductName = "Legacy Rose",
                    UnitPrice = 100000m,
                    Quantity = 1
                }
            ],
            Payments =
            [
                new Payment
                {
                    Method = PaymentMethod.CashOnDelivery,
                    Status = PaymentStatus.Pending,
                    Amount = 100000m,
                    Currency = "VND"
                }
            ]
        };

        db.AddRange(alice, bob, category, rose, addressA,
            addressB, cart, initialCartItem, legacyOrder);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        CheckoutRequestDto CheckoutRequest(
            Guid checkoutId, Guid addressId, string? note) => new()
        {
            CheckoutRequestId = checkoutId,
            ShippingAddressId = addressId,
            PaymentMethod = PaymentMethod.CashOnDelivery,
            Note = note
        };

        async Task<string> LoginAsync(User user)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginDto { Email = user.Email, Password = password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var login = await response.Content
                .ReadFromJsonAsync<LoginResponseDto>();
            return Assert.IsType<LoginResponseDto>(login).AccessToken;
        }

        // Per-request authorization avoids changing shared headers during
        // concurrent calls from the same or different users.
        async Task<HttpResponseMessage> PostCheckoutAsync(
            CheckoutRequestDto requestDto, string? token)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "/api/orders/checkout");
            if (token is not null)
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
            request.Content = JsonContent.Create(requestDto);
            return await client.SendAsync(request);
        }

        async Task<CheckoutResultDto> ExpectCheckoutAsync(
            CheckoutRequestDto dto, string token,
            HttpStatusCode expected, bool isReplay)
        {
            using var response = await PostCheckoutAsync(dto, token);
            Assert.Equal(expected, response.StatusCode);
            var result = await response.Content
                .ReadFromJsonAsync<CheckoutResultDto>();
            var checkout = Assert.IsType<CheckoutResultDto>(result);
            Assert.Equal(isReplay, checkout.IsReplay);
            return checkout;
        }

        async Task ExpectConflictAsync(
            CheckoutRequestDto dto, string token, string errorCode)
        {
            using var response = await PostCheckoutAsync(dto, token);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await response.Content
                .ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal(errorCode, Assert.IsType<ProblemDetails>(problem).Detail);
        }

        async Task<int> StockAsync() => await db.Products
            .AsNoTracking()
            .Where(x => x.Id == rose.Id)
            .Select(x => x.StockQuantity)
            .SingleAsync();

        async Task<int> UserOrderCountAsync() => await db.Orders
            .AsNoTracking()
            .CountAsync(x => x.UserId == alice.Id);

        try
        {
            var aliceToken = await LoginAsync(alice);
            var bobToken = await LoginAsync(bob);
            var checkoutId = Guid.NewGuid();
            var original = CheckoutRequest(checkoutId, addressA.Id, null);

            // 1. New checkout stores the fingerprint and reduces stock once.
            var created = await ExpectCheckoutAsync(
                original, aliceToken, HttpStatusCode.Created, isReplay: false);
            Assert.Equal($"FS-{checkoutId:N}", created.OrderCode);
            Assert.Equal(OrderStatus.Pending, created.Status);
            Assert.Equal(200000m, created.TotalAmount);
            Assert.Equal(6, await StockAsync());
            Assert.Equal(2, await UserOrderCountAsync()); // New + legacy.

            var persisted = await db.Orders.AsNoTracking()
                .SingleAsync(x => x.Id == created.OrderId);
            var expectedFingerprint =
                CheckoutRequestFingerprint.Compute(original);
            Assert.Equal(expectedFingerprint,
                persisted.CheckoutRequestFingerprint);
            Assert.Equal(64, persisted.CheckoutRequestFingerprint!.Length);
            Assert.Null(persisted.Note);
            Assert.Equal("Recipient One", persisted.RecipientName);
            Assert.False(await db.CartItems.AsNoTracking()
                .AnyAsync(x => x.CartId == cart.Id));

            // 2. Retrying the same payload returns the original order.
            var replay = await ExpectCheckoutAsync(
                original, aliceToken, HttpStatusCode.OK, isReplay: true);
            Assert.Equal(created.OrderId, replay.OrderId);
            Assert.Equal(created.OrderCode, replay.OrderCode);
            Assert.Equal(created.TotalAmount, replay.TotalAmount);
            Assert.Equal(6, await StockAsync());

            // 3. Changing any supported payload field is not a valid replay.
            await ExpectConflictAsync(
                CheckoutRequest(checkoutId, addressB.Id, null),
                aliceToken, "CHECKOUT_REQUEST_MISMATCH");
            await ExpectConflictAsync(
                CheckoutRequest(checkoutId, addressA.Id, ""),
                aliceToken, "CHECKOUT_REQUEST_MISMATCH");
            await ExpectConflictAsync(
                CheckoutRequest(checkoutId, addressA.Id, "Deliver tomorrow"),
                aliceToken, "CHECKOUT_REQUEST_MISMATCH");
            Assert.Equal(6, await StockAsync());
            Assert.Equal(2, await UserOrderCountAsync());

            // 4. Another Customer cannot obtain Alice's replayed order.
            await ExpectConflictAsync(original, bobToken,
                "CHECKOUT_REQUEST_ID_IN_USE");
            Assert.Equal(6, await StockAsync());

            // 5. A legacy order with a NULL fingerprint must not be replayed.
            await ExpectConflictAsync(
                CheckoutRequest(legacyRequestId, addressA.Id, null),
                aliceToken, "CHECKOUT_REQUEST_NOT_VERIFIABLE");
            Assert.Equal(6, await StockAsync());
            Assert.Equal(2, await UserOrderCountAsync());

            // 6. Changing current Product/Address records does not change
            // the original payload fingerprint or the Order snapshots.
            await db.Products
                .Where(x => x.Id == rose.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 900000m));
            await db.UserAddresses
                .Where(x => x.Id == addressA.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.RecipientName, "Updated Recipient"));

            var replayAfterEdit = await ExpectCheckoutAsync(
                original, aliceToken, HttpStatusCode.OK, isReplay: true);
            Assert.Equal(created.OrderId, replayAfterEdit.OrderId);
            Assert.Equal(100000m,
                Assert.Single(replayAfterEdit.Items).UnitPrice);
            Assert.Equal(6, await StockAsync());

            var storedOrder = await db.Orders.AsNoTracking()
                .SingleAsync(x => x.Id == created.OrderId);
            Assert.Equal("Recipient One", storedOrder.RecipientName);
            Assert.Equal(expectedFingerprint,
                storedOrder.CheckoutRequestFingerprint);

            // 7. A different CheckoutRequestId is a new checkout, so the
            // empty cart must return CART_EMPTY rather than replaying.
            await ExpectConflictAsync(
                CheckoutRequest(Guid.NewGuid(), addressA.Id, null),
                aliceToken, "CART_EMPTY");

            // 8. Concurrent requests using the same NEW id but different
            // Notes must not create two Orders or deduct stock twice.
            db.CartItems.Add(new CartItem
            {
                CartId = cart.Id,
                ProductId = rose.Id,
                Quantity = 1
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var concurrentId = Guid.NewGuid();
            var contenderA = CheckoutRequest(
                concurrentId, addressA.Id, "Morning");
            var contenderB = CheckoutRequest(
                concurrentId, addressA.Id, "Afternoon");

            var attempts = await Task.WhenAll(
                PostCheckoutAsync(contenderA, aliceToken),
                PostCheckoutAsync(contenderB, aliceToken));

            try
            {
                Assert.Equal(1, attempts.Count(x =>
                    x.StatusCode == HttpStatusCode.Created));
                Assert.Equal(1, attempts.Count(x =>
                    x.StatusCode == HttpStatusCode.Conflict));

                var conflictResponse = Assert.Single(
                    attempts, x => x.StatusCode == HttpStatusCode.Conflict);
                var problem = await conflictResponse.Content
                    .ReadFromJsonAsync<ProblemDetails>();
                Assert.Equal("CHECKOUT_REQUEST_MISMATCH",
                    Assert.IsType<ProblemDetails>(problem).Detail);
            }
            finally
            {
                foreach (var response in attempts)
                    response.Dispose();
            }

            Assert.Equal(5, await StockAsync());
            Assert.Equal(3, await UserOrderCountAsync()); // 2 new + legacy.
            Assert.False(await db.CartItems.AsNoTracking()
                .AnyAsync(x => x.CartId == cart.Id));

            var concurrentOrder = await db.Orders.AsNoTracking()
                .SingleAsync(x =>
                    x.OrderCode == $"FS-{concurrentId:N}");
            var fingerprintA = CheckoutRequestFingerprint.Compute(contenderA);
            var fingerprintB = CheckoutRequestFingerprint.Compute(contenderB);
            Assert.True(
                concurrentOrder.CheckoutRequestFingerprint == fingerprintA ||
                concurrentOrder.CheckoutRequestFingerprint == fingerprintB);
            Assert.Equal(1, await db.Payments.AsNoTracking()
                .CountAsync(x => x.OrderId == concurrentOrder.Id));
            Assert.Equal(1, await db.OrderItems.AsNoTracking()
                .CountAsync(x => x.OrderId == concurrentOrder.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orderIds = await db.Orders.AsNoTracking()
                .Where(x => x.UserId == alice.Id || x.UserId == bob.Id)
                .Select(x => x.Id)
                .ToArrayAsync();

            await db.Payments.Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();
            await db.OrderItems.Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();
            await db.Orders.Where(x => orderIds.Contains(x.Id))
                .ExecuteDeleteAsync();
            await db.CartItems.Where(x => x.CartId == cart.Id)
                .ExecuteDeleteAsync();
            await db.Carts.Where(x => x.Id == cart.Id)
                .ExecuteDeleteAsync();
            await db.UserAddresses.Where(x =>
                    x.Id == addressA.Id || x.Id == addressB.Id)
                .ExecuteDeleteAsync();
            await db.Products.Where(x => x.Id == rose.Id)
                .ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == alice.Id || x.Id == bob.Id)
                .ExecuteDeleteAsync();
        }
    }
}
