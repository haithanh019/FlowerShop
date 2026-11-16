using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.Carts;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public class CartHttpTests
{
    [Fact]
    public async Task CartCrud_And_Ownership_ShouldWork()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing test connection.");

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
            x => x.EndsWith("_InitialCreate"));

        const string password = "TestPassword#2026";

        User NewCustomer()
        {
            var user = new User
            {
                FullName = "Cart Integration Test",
                Email = $"test-{Guid.NewGuid():N}@example.invalid",
                Role = UserRole.Customer,
                IsActive = true
            };

            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var alice = NewCustomer();
        var bob = NewCustomer();

        var category = new Category
        {
            Name = "Cart Test Category",
            Slug = $"test-cart-category-{Guid.NewGuid():N}"
        };

        var product = new Product
        {
            Name = "Test Rose",
            Slug = $"test-cart-rose-{Guid.NewGuid():N}",
            Price = 100000m,
            StockQuantity = 8,
            IsActive = true,
            CategoryId = category.Id
        };

        var hiddenProduct = new Product
        {
            Name = "Hidden Rose",
            Slug = $"test-cart-hidden-{Guid.NewGuid():N}",
            Price = 80000m,
            StockQuantity = 5,
            IsActive = false,
            CategoryId = category.Id
        };

        var primaryImage = new ProductImage
        {
            ProductId = product.Id,
            ImageUrl = "https://example.invalid/cart-test.png",
            PublicId = null,
            IsPrimary = true,
            SortOrder = 0
        };

        db.AddRange(
            alice, bob, category,
            product, hiddenProduct, primaryImage);

        await db.SaveChangesAsync();

        try
        {
            // HTTP helpers
            async Task ExpectAsync(
                Task<HttpResponseMessage> request,
                HttpStatusCode expected)
            {
                using var response = await request;
                Assert.Equal(expected, response.StatusCode);
            }

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
                    HttpStatusCode.OK, response.StatusCode);

                var result = await response.Content
                    .ReadFromJsonAsync<LoginResponseDto>();

                return Assert.IsType<LoginResponseDto>(result)
                    .AccessToken;
            }

            async Task<CartDto> GetCartAsync()
            {
                using var response =
                    await client.GetAsync("/api/cart");

                Assert.Equal(
                    HttpStatusCode.OK, response.StatusCode);

                var result = await response.Content
                    .ReadFromJsonAsync<CartDto>();

                return Assert.IsType<CartDto>(result);
            }

            var addDto = new AddCartItemDto
            {
                ProductId = product.Id,
                Quantity = 2
            };

            // 1. Anonymous -> 401
            await ExpectAsync(
                client.GetAsync("/api/cart"),
                HttpStatusCode.Unauthorized);

            await ExpectAsync(
                client.PostAsJsonAsync("/api/cart/items", addDto),
                HttpStatusCode.Unauthorized);

            // 2. Login as Alice
            var aliceToken = await LoginAsync(alice);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", aliceToken);

            // 3. GET empty Cart must not create a Cart record
            var empty = await GetCartAsync();

            Assert.Null(empty.CartId);
            Assert.Empty(empty.Items);
            Assert.Equal(0, empty.TotalQuantity);
            Assert.Equal(0m, empty.Subtotal);

            Assert.False(await db.Carts
                .AsNoTracking()
                .AnyAsync(x => x.UserId == alice.Id));

            // 4. Add Product, quantity = 2
            await ExpectAsync(
                client.PostAsJsonAsync("/api/cart/items", addDto),
                HttpStatusCode.NoContent);

            var cart = await GetCartAsync();

            Assert.NotNull(cart.CartId);
            Assert.Equal(2, cart.TotalQuantity);
            Assert.Equal(200000m, cart.Subtotal);

            var item = Assert.Single(cart.Items);

            Assert.Equal(product.Id, item.ProductId);
            Assert.Equal("Test Rose", item.ProductName);
            Assert.Equal(100000m, item.UnitPrice);
            Assert.Equal(2, item.Quantity);
            Assert.Equal(200000m, item.LineTotal);
            Assert.True(item.IsAvailable);
            Assert.Equal(
                primaryImage.ImageUrl,
                item.PrimaryImageUrl);

            var cartItemId = item.Id;

            // 5. Add same Product again: 2 + 3 = 5
            await ExpectAsync(
                client.PostAsJsonAsync(
                    "/api/cart/items",
                    new AddCartItemDto
                    {
                        ProductId = product.Id,
                        Quantity = 3
                    }),
                HttpStatusCode.NoContent);

            cart = await GetCartAsync();

            Assert.Single(cart.Items);
            Assert.Equal(5, cart.TotalQuantity);
            Assert.Equal(500000m, cart.Subtotal);
            Assert.Equal(5, cart.Items[0].Quantity);

            // Stock is NOT deducted when adding to Cart
            var currentStock = await db.Products
                .AsNoTracking()
                .Where(x => x.Id == product.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

            Assert.Equal(8, currentStock);

            // 6. Update quantity from 5 to 4
            await ExpectAsync(
                client.PutAsJsonAsync(
                    $"/api/cart/items/{cartItemId}",
                    new UpdateCartItemDto { Quantity = 4 }),
                HttpStatusCode.NoContent);

            cart = await GetCartAsync();

            Assert.Equal(4, cart.TotalQuantity);
            Assert.Equal(400000m, cart.Subtotal);
            Assert.Equal(4, cart.Items[0].Quantity);

            // 7. Quantity = 0 -> 400
            await ExpectAsync(
                client.PutAsJsonAsync(
                    $"/api/cart/items/{cartItemId}",
                    new UpdateCartItemDto { Quantity = 0 }),
                HttpStatusCode.BadRequest);

            // 8. Update beyond stock -> 409
            await ExpectAsync(
                client.PutAsJsonAsync(
                    $"/api/cart/items/{cartItemId}",
                    new UpdateCartItemDto { Quantity = 9 }),
                HttpStatusCode.Conflict);

            // 9. Adding 5 more would exceed stock: 4 + 5 > 8
            await ExpectAsync(
                client.PostAsJsonAsync(
                    "/api/cart/items",
                    new AddCartItemDto
                    {
                        ProductId = product.Id,
                        Quantity = 5
                    }),
                HttpStatusCode.Conflict);

            // 10. Missing Product -> 404
            await ExpectAsync(
                client.PostAsJsonAsync(
                    "/api/cart/items",
                    new AddCartItemDto
                    {
                        ProductId = Guid.NewGuid(),
                        Quantity = 1
                    }),
                HttpStatusCode.NotFound);

            // 11. Empty ProductId -> 400
            await ExpectAsync(
                client.PostAsJsonAsync(
                    "/api/cart/items",
                    new AddCartItemDto
                    {
                        ProductId = Guid.Empty,
                        Quantity = 1
                    }),
                HttpStatusCode.BadRequest);

            // 12. Inactive Product -> 409
            await ExpectAsync(
                client.PostAsJsonAsync(
                    "/api/cart/items",
                    new AddCartItemDto
                    {
                        ProductId = hiddenProduct.Id,
                        Quantity = 1
                    }),
                HttpStatusCode.Conflict);

            // Failed operations must not change Alice's Cart
            cart = await GetCartAsync();

            Assert.Single(cart.Items);
            Assert.Equal(4, cart.Items[0].Quantity);

            // 13. Login as Bob
            var bobToken = await LoginAsync(bob);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", bobToken);

            var bobCart = await GetCartAsync();

            Assert.Null(bobCart.CartId);
            Assert.Empty(bobCart.Items);

            // Bob cannot update Alice's CartItem
            await ExpectAsync(
                client.PutAsJsonAsync(
                    $"/api/cart/items/{cartItemId}",
                    new UpdateCartItemDto { Quantity = 1 }),
                HttpStatusCode.NotFound);

            // Bob cannot delete Alice's CartItem
            await ExpectAsync(
                client.DeleteAsync(
                    $"/api/cart/items/{cartItemId}"),
                HttpStatusCode.NotFound);

            // 14. Bob creates his own Cart
            await ExpectAsync(
                client.PostAsJsonAsync(
                    "/api/cart/items",
                    new AddCartItemDto
                    {
                        ProductId = product.Id,
                        Quantity = 1
                    }),
                HttpStatusCode.NoContent);

            bobCart = await GetCartAsync();

            Assert.NotNull(bobCart.CartId);
            Assert.Equal(1, bobCart.TotalQuantity);
            Assert.NotEqual(cart.CartId, bobCart.CartId);

            // 15. Alice's Cart is unchanged
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", aliceToken);

            cart = await GetCartAsync();

            Assert.Equal(4, cart.TotalQuantity);
            Assert.Equal(400000m, cart.Subtotal);

            // 16. Stock drops below Cart quantity
            product.StockQuantity = 2;
            await db.SaveChangesAsync();

            cart = await GetCartAsync();

            Assert.Single(cart.Items);
            Assert.False(cart.Items[0].IsAvailable);
            Assert.Equal(4, cart.TotalQuantity);
            Assert.Equal(0m, cart.Subtotal);

            // Restore stock
            product.StockQuantity = 8;
            await db.SaveChangesAsync();

            // 17. Remove Alice's CartItem
            await ExpectAsync(
                client.DeleteAsync(
                    $"/api/cart/items/{cartItemId}"),
                HttpStatusCode.NoContent);

            cart = await GetCartAsync();

            Assert.NotNull(cart.CartId);
            Assert.Empty(cart.Items);
            Assert.Equal(0, cart.TotalQuantity);
            Assert.Equal(0m, cart.Subtotal);

            // 18. Delete same CartItem again -> 404
            await ExpectAsync(
                client.DeleteAsync(
                    $"/api/cart/items/{cartItemId}"),
                HttpStatusCode.NotFound);

            // Bob's Cart must remain unchanged
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", bobToken);

            bobCart = await GetCartAsync();

            Assert.Equal(1, bobCart.TotalQuantity);
            Assert.Single(bobCart.Items);

            // Only one Cart per User
            Assert.Equal(2, await db.Carts
                .AsNoTracking()
                .CountAsync(x =>
                    x.UserId == alice.Id ||
                    x.UserId == bob.Id));
        }
        finally
        {
            // Delete only records belonging to this test
            var cartIds = await db.Carts
                .AsNoTracking()
                .Where(x =>
                    x.UserId == alice.Id ||
                    x.UserId == bob.Id)
                .Select(x => x.Id)
                .ToArrayAsync();

            await db.CartItems
                .Where(x => cartIds.Contains(x.CartId))
                .ExecuteDeleteAsync();

            await db.Carts
                .Where(x => cartIds.Contains(x.Id))
                .ExecuteDeleteAsync();

            await db.ProductImages
                .Where(x => x.ProductId == product.Id)
                .ExecuteDeleteAsync();

            await db.Products
                .Where(x => x.CategoryId == category.Id)
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
