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

public sealed class OrderQueryHttpTests
{
    [Fact]
    public async Task OrderHistoryAndDetail_ShouldEnforceOwnershipAndReturnSnapshots()
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
                FullName = $"Order Query {label}",
                Email = $"order-query-{label.ToLowerInvariant()}-{suffix}@example.invalid",
                Role = role,
                IsActive = true
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var alice = NewUser("Alice", UserRole.Customer);
        var bob = NewUser("Bob", UserRole.Customer);
        var admin = NewUser("Admin", UserRole.Admin);
        var charlie = NewUser("Charlie", UserRole.Customer);

        var category = new Category
        {
            Name = "Order Query Test Category",
            Slug = $"order-query-category-{suffix}"
        };
        var rose = new Product
        {
            Name = "Rose Current Name",
            Slug = $"order-query-rose-{suffix}",
            CategoryId = category.Id,
            Price = 999000m,       // Different from the OrderItem snapshot
            StockQuantity = 10,
            IsActive = true
        };
        var tulip = new Product
        {
            Name = "Tulip Current Name",
            Slug = $"order-query-tulip-{suffix}",
            CategoryId = category.Id,
            Price = 99000m,
            StockQuantity = 10,
            IsActive = true
        };

        var now = DateTime.UtcNow;

        Order NewOrder(User owner, DateTime createdAt,
            int quantity, decimal unitPrice, string snapshotName,
            bool addPayment = true)
        {
            var amount = quantity * unitPrice;
            var order = new Order
            {
                UserId = owner.Id,
                OrderCode = $"FS-{Guid.NewGuid():N}",
                Status = OrderStatus.Pending,
                CreatedAt = createdAt,
                RecipientName = "Original Recipient",
                RecipientPhone = "0900000000",
                ShippingAddressLine = "123 Original Street",
                ShippingWard = "Original Ward",
                ShippingDistrict = "Original District",
                ShippingProvince = "Da Nang",
                ShippingCountryCode = "VN",
                ShippingPostalCode = "550000",
                Note = "Gift wrapping",
                Subtotal = amount,
                ShippingFee = 0m,
                DiscountAmount = 0m,
                TotalAmount = amount,
                Items =
                [
                    new OrderItem
                    {
                        ProductId = rose.Id,
                        ProductName = snapshotName,
                        UnitPrice = unitPrice,
                        Quantity = quantity
                    }
                ]
            };
            if (addPayment)
            {
                order.Payments.Add(new Payment
                {
                    Method = PaymentMethod.CashOnDelivery,
                    Status = PaymentStatus.Pending,
                    Amount = amount,
                    Currency = "VND",
                    CreatedAt = createdAt
                });
            }
            return order;
        }

        var oldest = NewOrder(alice, now.AddDays(-3),
            1, 100000m, "Old Rose Snapshot", addPayment: false);
        var middle = NewOrder(alice, now.AddDays(-2),
            4, 110000m, "Middle Rose Snapshot");
        var newest = NewOrder(alice, now.AddDays(-1),
            2, 120000m, "Rose Snapshot");

        newest.Items.Add(new OrderItem
        {
            ProductId = tulip.Id,
            ProductName = "Tulip Snapshot",
            UnitPrice = 80000m,
            Quantity = 1
        });
        newest.Subtotal = 320000m;
        newest.TotalAmount = 320000m;
        var firstPayment = newest.Payments.Single();
        firstPayment.Amount = 320000m;
        firstPayment.Status = PaymentStatus.Failed;
        firstPayment.CreatedAt = now.AddDays(-1).AddMinutes(1);

        var latestPayment = new Payment
        {
            Method = PaymentMethod.CashOnDelivery,
            Status = PaymentStatus.Pending,
            Amount = 320000m,
            Currency = "VND",
            CreatedAt = now.AddDays(-1).AddMinutes(2)
        };
        newest.Payments.Add(latestPayment);

        var bobOrder = NewOrder(bob, now,
            1, 130000m, "Bob's Private Rose");

        db.AddRange(alice, bob, admin, charlie, category, rose, tulip,
            oldest, middle, newest, bobOrder);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        try
        {
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

            async Task ExpectStatusAsync(string url, HttpStatusCode status)
            {
                using var response = await client.GetAsync(url);
                Assert.Equal(status, response.StatusCode);
            }

            async Task<T> GetDtoAsync<T>(string url)
            {
                using var response = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = await response.Content.ReadFromJsonAsync<T>();
                return Assert.IsType<T>(result);
            }

            // 1. Authentication and role authorization.
            await ExpectStatusAsync("/api/orders", HttpStatusCode.Unauthorized);
            await ExpectStatusAsync($"/api/orders/{newest.Id}",
                HttpStatusCode.Unauthorized);

            UseToken(await LoginAsync(admin));
            await ExpectStatusAsync("/api/orders", HttpStatusCode.Forbidden);
            await ExpectStatusAsync($"/api/orders/{newest.Id}",
                HttpStatusCode.Forbidden);

            // 2. Alice can only see her own three orders.
            UseToken(await LoginAsync(alice));
            var history = await GetDtoAsync<OrderHistoryDto>("/api/orders");
            Assert.Equal(1, history.Page);
            Assert.Equal(10, history.PageSize);
            Assert.Equal(3, history.TotalCount);
            Assert.Equal(1, history.TotalPages);
            Assert.Equal(3, history.Items.Count);
            Assert.Equal(newest.Id, history.Items[0].OrderId);
            Assert.Equal(middle.Id, history.Items[1].OrderId);
            Assert.Equal(oldest.Id, history.Items[2].OrderId);
            Assert.DoesNotContain(history.Items, x => x.OrderId == bobOrder.Id);
            Assert.Equal(3, history.Items[0].TotalQuantity);
            Assert.Equal(320000m, history.Items[0].TotalAmount);
            Assert.Equal(PaymentStatus.Pending, history.Items[0].PaymentStatus);
            Assert.Equal(PaymentMethod.CashOnDelivery,
                history.Items[0].PaymentMethod);
            Assert.Null(history.Items[2].PaymentMethod);
            Assert.Null(history.Items[2].PaymentStatus);

            // 3. Pagination happens before DTO mapping.
            var page1 = await GetDtoAsync<OrderHistoryDto>(
                "/api/orders?page=1&pageSize=1");
            var page2 = await GetDtoAsync<OrderHistoryDto>(
                "/api/orders?page=2&pageSize=1");
            var page3 = await GetDtoAsync<OrderHistoryDto>(
                "/api/orders?page=3&pageSize=1");
            var page4 = await GetDtoAsync<OrderHistoryDto>(
                "/api/orders?page=4&pageSize=1");
            Assert.Equal(3, page1.TotalCount);
            Assert.Equal(3, page1.TotalPages);
            Assert.Equal(newest.Id, Assert.Single(page1.Items).OrderId);
            Assert.Equal(middle.Id, Assert.Single(page2.Items).OrderId);
            Assert.Equal(oldest.Id, Assert.Single(page3.Items).OrderId);
            Assert.Empty(page4.Items);
            Assert.Equal(3, page4.TotalCount);

            // 4. Invalid pagination must be rejected.
            await ExpectStatusAsync("/api/orders?page=0", HttpStatusCode.BadRequest);
            await ExpectStatusAsync("/api/orders?pageSize=0", HttpStatusCode.BadRequest);
            await ExpectStatusAsync("/api/orders?pageSize=51", HttpStatusCode.BadRequest);
            await ExpectStatusAsync(
                "/api/orders?page=2147483647&pageSize=50",
                HttpStatusCode.BadRequest);

            // 5. Detail uses saved product/shipping snapshots, not Product data.
            var detail = await GetDtoAsync<OrderDetailDto>(
                $"/api/orders/{newest.Id}");
            Assert.Equal(newest.OrderCode, detail.OrderCode);
            Assert.Equal("Original Recipient", detail.RecipientName);
            Assert.Equal("123 Original Street", detail.ShippingAddressLine);
            Assert.Equal("Original Ward", detail.ShippingWard);
            Assert.Equal("Gift wrapping", detail.Note);
            Assert.Equal(320000m, detail.Subtotal);
            Assert.Equal(320000m, detail.TotalAmount);
            Assert.Equal(2, detail.Items.Count);
            Assert.Contains(detail.Items, x =>
                x.ProductId == rose.Id &&
                x.ProductName == "Rose Snapshot" &&
                x.UnitPrice == 120000m &&
                x.Quantity == 2 && x.LineTotal == 240000m);
            Assert.Contains(detail.Items, x =>
                x.ProductId == tulip.Id &&
                x.ProductName == "Tulip Snapshot" &&
                x.UnitPrice == 80000m &&
                x.Quantity == 1 && x.LineTotal == 80000m);
            Assert.Equal(2, detail.Payments.Count);
            Assert.Equal(latestPayment.Id, detail.Payments[0].PaymentId);
            Assert.Equal(PaymentStatus.Pending, detail.Payments[0].Status);
            Assert.Equal(PaymentStatus.Failed, detail.Payments[1].Status);
            Assert.Equal("VND", detail.Payments[0].Currency);

            // 6. A missing order or another user's order is always 404.
            await ExpectStatusAsync($"/api/orders/{bobOrder.Id}",
                HttpStatusCode.NotFound);
            await ExpectStatusAsync($"/api/orders/{Guid.NewGuid()}",
                HttpStatusCode.NotFound);
            await ExpectStatusAsync($"/api/orders/{Guid.Empty}",
                HttpStatusCode.BadRequest);

            // 7. Bob's history and detail must not expose Alice's orders.
            UseToken(await LoginAsync(bob));
            var bobHistory = await GetDtoAsync<OrderHistoryDto>("/api/orders");
            Assert.Equal(1, bobHistory.TotalCount);
            Assert.Equal(bobOrder.Id, Assert.Single(bobHistory.Items).OrderId);
            await ExpectStatusAsync($"/api/orders/{newest.Id}",
                HttpStatusCode.NotFound);
            var bobDetail = await GetDtoAsync<OrderDetailDto>(
                $"/api/orders/{bobOrder.Id}");
            Assert.Equal("Bob's Private Rose",
                Assert.Single(bobDetail.Items).ProductName);

            // 8. A customer without Orders gets an empty page.
            UseToken(await LoginAsync(charlie));
            var emptyHistory = await GetDtoAsync<OrderHistoryDto>(
                "/api/orders?page=1&pageSize=10");
            Assert.Empty(emptyHistory.Items);
            Assert.Equal(0, emptyHistory.TotalCount);
            Assert.Equal(0, emptyHistory.TotalPages);

            // 9. Query endpoints must not modify persisted Orders.
            Assert.Equal(4, await db.Orders.AsNoTracking()
                .CountAsync(x => x.UserId == alice.Id || x.UserId == bob.Id));
            Assert.Equal(999000m, await db.Products.AsNoTracking()
                .Where(x => x.Id == rose.Id)
                .Select(x => x.Price).SingleAsync());
        }
        finally
        {
            db.ChangeTracker.Clear();
            var ids = new[] { oldest.Id, middle.Id, newest.Id, bobOrder.Id };

            // Delete in foreign-key order. Only remove this test's records.
            await db.Payments.Where(x => ids.Contains(x.OrderId))
                .ExecuteDeleteAsync();
            await db.OrderItems.Where(x => ids.Contains(x.OrderId))
                .ExecuteDeleteAsync();
            await db.Orders.Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync();
            await db.Products.Where(x => x.Id == rose.Id || x.Id == tulip.Id)
                .ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == alice.Id ||
                                      x.Id == bob.Id ||
                                      x.Id == admin.Id ||
                                      x.Id == charlie.Id)
                .ExecuteDeleteAsync();
        }
    }
}
