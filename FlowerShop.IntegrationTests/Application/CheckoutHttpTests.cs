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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public class CheckoutHttpTests
{
    [Fact]
    public async Task CheckoutHttp_ShouldValidateCreateReplayAndPreserveSnapshots()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing test connection.");

        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test",
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

        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        const string password = "TestPassword#2026";
        var suffix = Guid.NewGuid().ToString("N");

        User NewUser(string label, UserRole role)
        {
            var user = new User
            {
                FullName = label,
                Email = $"checkout-{label.ToLowerInvariant()}-{suffix}@example.invalid",
                Role = role,
                IsActive = true
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var alice = NewUser("Alice", UserRole.Customer);
        var bob = NewUser("Bob", UserRole.Customer);
        var admin = NewUser("Admin", UserRole.Admin);

        var category = new Category
        {
            Name = "Checkout HTTP Test",
            Slug = $"checkout-http-category-{suffix}"
        };

        var rose = new Product
        {
            Name = "Rose Bouquet",
            Slug = $"checkout-http-rose-{suffix}",
            CategoryId = category.Id,
            Price = 120000m,
            StockQuantity = 6,
            IsActive = true
        };
        var tulip = new Product
        {
            Name = "Tulip Bouquet",
            Slug = $"checkout-http-tulip-{suffix}",
            CategoryId = category.Id,
            Price = 80000m,
            StockQuantity = 1,
            IsActive = true
        };

        var aliceAddress = new UserAddress
        {
            UserId = alice.Id,
            RecipientName = "Alice Recipient",
            RecipientPhone = "0900000001",
            AddressLine = "1 Flower Street",
            Ward = "Ward A",
            District = "District A",
            Province = "Da Nang",
            CountryCode = "VN",
            PostalCode = "550000"
        };
        var bobAddress = new UserAddress
        {
            UserId = bob.Id,
            RecipientName = "Bob Recipient",
            RecipientPhone = "0900000002",
            AddressLine = "2 Flower Street",
            Ward = "Ward B",
            District = "District B",
            Province = "Da Nang",
            CountryCode = "VN"
        };

        var cart = new Cart { UserId = alice.Id };
        var roseItem = new CartItem
        {
            CartId = cart.Id,
            ProductId = rose.Id,
            Quantity = 2
        };
        var tulipItem = new CartItem
        {
            CartId = cart.Id,
            ProductId = tulip.Id,
            Quantity = 1
        };

        db.AddRange(alice, bob, admin, category, rose, tulip,
            aliceAddress, bobAddress, cart, roseItem, tulipItem);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        try
        {
            const string url = "/api/orders/checkout";

            async Task<string> LoginAsync(User user)
            {
                using var response = await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new LoginDto
                    {
                        Email = user.Email,
                        Password = password
                    });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var login = await response.Content
                    .ReadFromJsonAsync<LoginResponseDto>();
                return Assert.IsType<LoginResponseDto>(login).AccessToken;
            }

            void UseToken(string? token) =>
                client.DefaultRequestHeaders.Authorization = token is null
                    ? null
                    : new AuthenticationHeaderValue("Bearer", token);

            async Task ExpectStatusAsync(
                CheckoutRequestDto request, HttpStatusCode expected)
            {
                using var response = await client.PostAsJsonAsync(url, request);
                Assert.Equal(expected, response.StatusCode);
            }

            async Task<CheckoutResultDto> ExpectResultAsync(
                CheckoutRequestDto request, HttpStatusCode expected)
            {
                using var response = await client.PostAsJsonAsync(url, request);
                Assert.Equal(expected, response.StatusCode);
                var result = await response.Content
                    .ReadFromJsonAsync<CheckoutResultDto>();
                return Assert.IsType<CheckoutResultDto>(result);
            }

            var request = new CheckoutRequestDto
            {
                CheckoutRequestId = Guid.NewGuid(),
                ShippingAddressId = aliceAddress.Id,
                PaymentMethod = PaymentMethod.CashOnDelivery,
                Note = "Deliver in the morning"
            };

            // 1. No JWT -> 401.
            await ExpectStatusAsync(request, HttpStatusCode.Unauthorized);

            // 2. Admin cannot checkout -> 403.
            UseToken(await LoginAsync(admin));
            await ExpectStatusAsync(request, HttpStatusCode.Forbidden);

            // 3. Another customer cannot use Alice's address -> 404.
            var bobToken = await LoginAsync(bob);
            UseToken(bobToken);
            await ExpectStatusAsync(request, HttpStatusCode.NotFound);

            // 4. Input validation for an authenticated customer.
            var aliceToken = await LoginAsync(alice);
            UseToken(aliceToken);

            await ExpectStatusAsync(new CheckoutRequestDto
            {
                CheckoutRequestId = Guid.Empty,
                ShippingAddressId = aliceAddress.Id,
                PaymentMethod = PaymentMethod.CashOnDelivery
            }, HttpStatusCode.BadRequest);

            await ExpectStatusAsync(new CheckoutRequestDto
            {
                CheckoutRequestId = Guid.NewGuid(),
                ShippingAddressId = aliceAddress.Id,
                PaymentMethod = PaymentMethod.VNPay
            }, HttpStatusCode.BadRequest);

            await ExpectStatusAsync(new CheckoutRequestDto
            {
                CheckoutRequestId = Guid.NewGuid(),
                ShippingAddressId = bobAddress.Id,
                PaymentMethod = PaymentMethod.CashOnDelivery
            }, HttpStatusCode.NotFound);

            // 5. Product inactive -> 409, no order or stock change.
            await db.Products.Where(x => x.Id == tulip.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            await ExpectStatusAsync(request, HttpStatusCode.Conflict);
            await db.Products.Where(x => x.Id == tulip.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true));

            // 6. Insufficient stock -> 409, Cart remains unchanged.
            await db.Products.Where(x => x.Id == tulip.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.StockQuantity, 0));
            await ExpectStatusAsync(request, HttpStatusCode.Conflict);
            await db.Products.Where(x => x.Id == tulip.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.StockQuantity, 1));

            Assert.False(await db.Orders.AsNoTracking()
                .AnyAsync(x => x.UserId == alice.Id));
            Assert.Equal(2, await db.CartItems.AsNoTracking()
                .CountAsync(x => x.CartId == cart.Id));
            Assert.Equal(6, await db.Products.AsNoTracking()
                .Where(x => x.Id == rose.Id)
                .Select(x => x.StockQuantity).SingleAsync());

            // 7. Successful COD checkout -> 201.
            var created = await ExpectResultAsync(
                request, HttpStatusCode.Created);

            Assert.False(created.IsReplay);
            Assert.Equal($"FS-{request.CheckoutRequestId:N}", created.OrderCode);
            Assert.Equal(OrderStatus.Pending, created.Status);
            Assert.Equal(PaymentMethod.CashOnDelivery, created.PaymentMethod);
            Assert.Equal(PaymentStatus.Pending, created.PaymentStatus);
            Assert.Equal(320000m, created.Subtotal);
            Assert.Equal(0m, created.ShippingFee);
            Assert.Equal(0m, created.DiscountAmount);
            Assert.Equal(320000m, created.TotalAmount);
            Assert.Equal(2, created.Items.Count);
            Assert.Contains(created.Items, x =>
                x.ProductId == rose.Id && x.ProductName == "Rose Bouquet" &&
                x.Quantity == 2 && x.UnitPrice == 120000m &&
                x.LineTotal == 240000m);
            Assert.Contains(created.Items, x =>
                x.ProductId == tulip.Id && x.ProductName == "Tulip Bouquet" &&
                x.Quantity == 1 && x.UnitPrice == 80000m &&
                x.LineTotal == 80000m);

            var persisted = await db.Orders.AsNoTracking()
                .SingleAsync(x => x.Id == created.OrderId);
            Assert.Equal(alice.Id, persisted.UserId);
            Assert.Equal("Alice Recipient", persisted.RecipientName);
            Assert.Equal("1 Flower Street", persisted.ShippingAddressLine);
            Assert.Equal("Ward A", persisted.ShippingWard);
            Assert.Equal("Da Nang", persisted.ShippingProvince);
            Assert.Equal("Deliver in the morning", persisted.Note);
            Assert.Equal(320000m, persisted.TotalAmount);

            var storedItems = await db.OrderItems.AsNoTracking()
                .Where(x => x.OrderId == created.OrderId)
                .ToListAsync();
            Assert.Equal(2, storedItems.Count);
            Assert.Contains(storedItems, x =>
                x.ProductId == rose.Id && x.ProductName == "Rose Bouquet" &&
                x.UnitPrice == 120000m && x.Quantity == 2);
            Assert.Contains(storedItems, x =>
                x.ProductId == tulip.Id && x.UnitPrice == 80000m &&
                x.Quantity == 1);

            var payment = await db.Payments.AsNoTracking()
                .SingleAsync(x => x.OrderId == created.OrderId);
            Assert.Equal(PaymentMethod.CashOnDelivery, payment.Method);
            Assert.Equal(PaymentStatus.Pending, payment.Status);
            Assert.Equal(320000m, payment.Amount);
            Assert.Equal("VND", payment.Currency);
            Assert.Null(payment.PaidAt);

            Assert.Equal(4, await db.Products.AsNoTracking()
                .Where(x => x.Id == rose.Id)
                .Select(x => x.StockQuantity).SingleAsync());
            Assert.Equal(0, await db.Products.AsNoTracking()
                .Where(x => x.Id == tulip.Id)
                .Select(x => x.StockQuantity).SingleAsync());
            Assert.False(await db.CartItems.AsNoTracking()
                .AnyAsync(x => x.CartId == cart.Id));
            Assert.True(await db.Carts.AsNoTracking()
                .AnyAsync(x => x.Id == cart.Id));

            // 8. Changing Product and Address cannot rewrite Order snapshots.
            await db.Products.Where(x => x.Id == rose.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 999000m));
            await db.UserAddresses.Where(x => x.Id == aliceAddress.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.RecipientName, "Updated Recipient"));

            var orderAfterChange = await db.Orders.AsNoTracking()
                .SingleAsync(x => x.Id == created.OrderId);
            var roseSnapshot = await db.OrderItems.AsNoTracking()
                .SingleAsync(x => x.OrderId == created.OrderId &&
                                  x.ProductId == rose.Id);
            Assert.Equal("Alice Recipient", orderAfterChange.RecipientName);
            Assert.Equal(120000m, roseSnapshot.UnitPrice);
            Assert.Equal(320000m, orderAfterChange.TotalAmount);

            // 9. Same request ID -> 200, same Order, no second deduction.
            var replay = await ExpectResultAsync(request, HttpStatusCode.OK);
            Assert.True(replay.IsReplay);
            Assert.Equal(created.OrderId, replay.OrderId);
            Assert.Equal(created.OrderCode, replay.OrderCode);
            Assert.Equal(created.TotalAmount, replay.TotalAmount);
            Assert.Contains(replay.Items, x =>
                x.ProductId == rose.Id && x.UnitPrice == 120000m);

            Assert.Equal(1, await db.Orders.AsNoTracking()
                .CountAsync(x => x.UserId == alice.Id));
            Assert.Equal(1, await db.Payments.AsNoTracking()
                .CountAsync(x => x.OrderId == created.OrderId));
            Assert.Equal(4, await db.Products.AsNoTracking()
                .Where(x => x.Id == rose.Id)
                .Select(x => x.StockQuantity).SingleAsync());

            // 10. New request ID with empty Cart -> 409.
            await ExpectStatusAsync(new CheckoutRequestDto
            {
                CheckoutRequestId = Guid.NewGuid(),
                ShippingAddressId = aliceAddress.Id,
                PaymentMethod = PaymentMethod.CashOnDelivery
            }, HttpStatusCode.Conflict);

            // 11. Bob cannot replay Alice's CheckoutRequestId -> 409.
            UseToken(bobToken);
            await ExpectStatusAsync(request, HttpStatusCode.Conflict);
        }
        finally
        {
            db.ChangeTracker.Clear();

            // Clean up only records created by this test.
            var orderIds = await db.Orders.AsNoTracking()
                .Where(x => x.UserId == alice.Id || x.UserId == bob.Id)
                .Select(x => x.Id).ToArrayAsync();

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
                x.Id == aliceAddress.Id || x.Id == bobAddress.Id)
                .ExecuteDeleteAsync();
            await db.Products.Where(x =>
                x.Id == rose.Id || x.Id == tulip.Id)
                .ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();
            await db.Users.Where(x =>
                x.Id == alice.Id || x.Id == bob.Id || x.Id == admin.Id)
                .ExecuteDeleteAsync();
        }
    }
}
