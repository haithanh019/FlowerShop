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
using System.Data.SqlTypes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class AdminOrderQueryHttpTests
{
    [Fact]
    public async Task AdminOrderQuery_ShouldEnforceSecurityAndReturnSnapshots()
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
            x => x.EndsWith("_InitialCreate"));

        const string password = "TestPassword#2026";
        const string endpoint = "/api/admin/orders";

        var suffix = Guid.NewGuid().ToString("N");
        var tag = suffix[..20];
        var now = DateTime.UtcNow;

        User NewUser(string label, UserRole role)
        {
            var user = new User
            {
                FullName = $"Admin Query {label}",
                Email = $"admin-query-{label}-{suffix}@example.invalid",
                Role = role,
                IsActive = true
            };

            user.PasswordHash =
                hasher.HashPassword(user, password);

            return user;
        }

        var alice = NewUser("alice", UserRole.Customer);
        var bob = NewUser("bob", UserRole.Customer);
        var admin = NewUser("admin", UserRole.Admin);

        var category = new Category
        {
            Name = "Admin Query HTTP Category",
            Slug = $"admin-query-category-{suffix}"
        };

        var rose = new Product
        {
            Name = "Current Rose Name",
            Slug = $"admin-query-rose-{suffix}",
            CategoryId = category.Id,
            Price = 999000m,
            StockQuantity = 10,
            IsActive = true
        };

        Order NewOrder(
            User owner,
            string label,
            OrderStatus status,
            DateTime createdAt,
            int quantity,
            decimal unitPrice,
            string snapshotName)
        {
            var amount = quantity * unitPrice;

            return new Order
            {
                UserId = owner.Id,
                OrderCode = $"FS-{label}-{tag}",
                Status = status,
                CreatedAt = createdAt,

                RecipientName = $"{owner.FullName} Recipient",
                RecipientPhone = "0900000000",
                ShippingAddressLine = "123 Original Street",
                ShippingWard = "Original Ward",
                ShippingDistrict = "Original District",
                ShippingProvince = "Da Nang",
                ShippingCountryCode = "VN",
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
                ],

                Payments =
                [
                    new Payment
                    {
                        Method = PaymentMethod.CashOnDelivery,
                        Status = PaymentStatus.Pending,
                        Amount = amount,
                        Currency = "VND",
                        CreatedAt = createdAt.AddMinutes(1)
                    }
                ]
            };
        }

        var aliceOld = NewOrder(
            alice, "A-OLD", OrderStatus.Pending,
            now.AddDays(-3), 1, 100000m,
            "Old Rose Snapshot");

        var aliceNew = NewOrder(
            alice, "A-NEW", OrderStatus.Confirmed,
            now.AddDays(-1), 2, 120000m,
            "New Rose Snapshot");

        var bobShipping = NewOrder(
            bob, "B-SHIP", OrderStatus.Shipping,
            now, 3, 70000m,
            "Shipping Rose Snapshot");

        var bobTie = NewOrder(
            bob, "B-TIE", OrderStatus.Pending,
            now, 1, 80000m,
            "Tie Rose Snapshot");

        // Exercise the latest-payment selection policy.
        bobShipping.Payments.Add(new Payment
        {
            Method = PaymentMethod.CashOnDelivery,
            Status = PaymentStatus.Failed,
            Amount = 210000m,
            Currency = "VND",
            CreatedAt = now.AddMinutes(-5)
        });

        var orders = new[]
        {
            aliceOld, aliceNew, bobShipping, bobTie
        };

        db.AddRange(alice, bob, admin, category, rose);
        db.Orders.AddRange(orders);

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

        async Task<HttpResponseMessage> GetAsync(
            string url,
            string? token = null)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, url);

            if (token is not null)
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer", token);
            }

            return await client.SendAsync(request);
        }

        async Task ExpectStatusAsync(
            string url,
            string? token,
            HttpStatusCode expected)
        {
            using var response = await GetAsync(url, token);

            Assert.Equal(expected, response.StatusCode);
        }

        async Task<T> GetDtoAsync<T>(
            string url,
            string token)
        {
            using var response = await GetAsync(url, token);

            Assert.Equal(
                HttpStatusCode.OK,
                response.StatusCode);

            var result = await response.Content
                .ReadFromJsonAsync<T>();

            return Assert.IsType<T>(result);
        }

        try
        {
            var adminToken = await LoginAsync(admin);
            var aliceToken = await LoginAsync(alice);

            // 1. Anonymous requests must receive 401.
            await ExpectStatusAsync(
                endpoint, null, HttpStatusCode.Unauthorized);

            await ExpectStatusAsync(
                $"{endpoint}/{aliceOld.Id}",
                null, HttpStatusCode.Unauthorized);

            // 2. Customer requests must receive 403.
            await ExpectStatusAsync(
                endpoint, aliceToken, HttpStatusCode.Forbidden);

            await ExpectStatusAsync(
                $"{endpoint}/{bobShipping.Id}",
                aliceToken, HttpStatusCode.Forbidden);

            // 3. Admin can list Orders across all Customers.
            // Use a test-specific tag so unrelated test data
            // never changes the expected TotalCount.
            var all = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?orderCodeKeyword={tag}",
                adminToken);

            Assert.Equal(4, all.TotalCount);
            Assert.Equal(4, all.Items.Count);
            Assert.Equal(1, all.Page);
            Assert.Equal(10, all.PageSize);
            Assert.Equal(1, all.TotalPages);

            Assert.Contains(all.Items,
                x => x.CustomerUserId == alice.Id &&
                     x.CustomerEmail == alice.Email);

            Assert.Contains(all.Items,
                x => x.CustomerUserId == bob.Id &&
                     x.CustomerEmail == bob.Email);

            // 4. Sorting must be stable even when timestamps tie.
            var expectedIds = orders
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => new SqlGuid(x.Id.ToByteArray()))
                .Select(x => x.Id)
                .ToArray();


            Assert.Equal(
                expectedIds,
                all.Items.Select(x => x.OrderId).ToArray());

            // 5. Pagination must preserve order and count.
            var firstPage = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?orderCodeKeyword={tag}&page=1&pageSize=2",
                adminToken);

            var secondPage = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?orderCodeKeyword={tag}&page=2&pageSize=2",
                adminToken);

            var emptyPage = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?orderCodeKeyword={tag}&page=3&pageSize=2",
                adminToken);

            Assert.Equal(4, firstPage.TotalCount);
            Assert.Equal(2, firstPage.TotalPages);
            Assert.Equal(4, secondPage.TotalCount);

            Assert.Equal(
                [.. expectedIds.Take(2)],
                [.. firstPage.Items.Select(x => x.OrderId)]);

            Assert.Equal(
                [.. expectedIds.Skip(2)],
                [.. secondPage.Items.Select(x => x.OrderId)]);

            Assert.Empty(emptyPage.Items);
            Assert.Equal(4, emptyPage.TotalCount);

            // 6. Filter by OrderStatus.
            var pending = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?orderCodeKeyword={tag}&status=0",
                adminToken);

            Assert.Equal(2, pending.TotalCount);
            Assert.All(pending.Items,
                x => Assert.Equal(OrderStatus.Pending, x.Status));

            // 7. Filter by CustomerUserId.
            var aliceOrders = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?customerUserId={alice.Id}",
                adminToken);

            Assert.Equal(2, aliceOrders.TotalCount);
            Assert.All(aliceOrders.Items,
                x => Assert.Equal(alice.Id, x.CustomerUserId));

            // 8. Combine Status, Customer and OrderCode.
            var combined = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?status=0" +
                $"&customerUserId={bob.Id}" +
                "&orderCodeKeyword=B-TIE",
                adminToken);

            Assert.Equal(1, combined.TotalCount);
            Assert.Equal(
                bobTie.Id,
                Assert.Single(combined.Items).OrderId);

            // 9. Whitespace in search keyword is trimmed.
            var keyword = Uri.EscapeDataString($" {tag} ");

            var trimmedSearch = await GetDtoAsync<AdminOrderPageDto>(
                $"{endpoint}?orderCodeKeyword={keyword}",
                adminToken);

            Assert.Equal(4, trimmedSearch.TotalCount);

            // 10. Validate bad query parameters.
            var badQueries = new[]
            {
                $"{endpoint}?page=0",
                $"{endpoint}?pageSize=0",
                $"{endpoint}?pageSize=51",
                $"{endpoint}?page=2147483647&pageSize=50",
                $"{endpoint}?status=999",
                $"{endpoint}?status=InvalidStatus",
                $"{endpoint}?customerUserId={Guid.Empty}",
                $"{endpoint}?orderCodeKeyword={new string('X', 41)}"
            };

            foreach (var url in badQueries)
            {
                await ExpectStatusAsync(
                    url, adminToken, HttpStatusCode.BadRequest);
            }

            // 11. Admin can read another Customer's Order.
            var detail = await GetDtoAsync<AdminOrderDetailDto>(
                $"{endpoint}/{aliceNew.Id}",
                adminToken);

            Assert.Equal(alice.Id, detail.CustomerUserId);
            Assert.Equal(alice.Email, detail.CustomerEmail);

            Assert.Equal(aliceNew.Id, detail.Order.OrderId);
            Assert.Equal(aliceNew.OrderCode, detail.Order.OrderCode);
            Assert.Equal(OrderStatus.Confirmed, detail.Order.Status);

            Assert.Equal(
                "123 Original Street",
                detail.Order.ShippingAddressLine);

            Assert.Equal("Gift wrapping", detail.Order.Note);
            Assert.Equal(240000m, detail.Order.TotalAmount);

            var orderItem = Assert.Single(detail.Order.Items);
            Assert.Equal("New Rose Snapshot", orderItem.ProductName);
            Assert.Equal(120000m, orderItem.UnitPrice);
            Assert.Equal(2, orderItem.Quantity);
            Assert.Equal(240000m, orderItem.LineTotal);

            var payment = Assert.Single(detail.Order.Payments);
            Assert.Equal(
                PaymentMethod.CashOnDelivery,
                payment.Method);
            Assert.Equal(PaymentStatus.Pending, payment.Status);
            Assert.Equal(240000m, payment.Amount);

            // 12. Admin summary must use latest Payment.
            var shippingSummary = Assert.Single(
                all.Items,
                x => x.OrderId == bobShipping.Id);

            Assert.Equal(3, shippingSummary.TotalQuantity);
            Assert.Equal(210000m, shippingSummary.TotalAmount);
            Assert.Equal(
                PaymentStatus.Pending,
                shippingSummary.PaymentStatus);

            var shippingDetail = await GetDtoAsync<AdminOrderDetailDto>(
                $"{endpoint}/{bobShipping.Id}",
                adminToken);

            Assert.Equal(2, shippingDetail.Order.Payments.Count);
            Assert.Equal(
                PaymentStatus.Pending,
                shippingDetail.Order.Payments[0].Status);
            Assert.Equal(
                PaymentStatus.Failed,
                shippingDetail.Order.Payments[1].Status);

            // 13. Missing and empty Order IDs.
            await ExpectStatusAsync(
                $"{endpoint}/{Guid.NewGuid()}",
                adminToken,
                HttpStatusCode.NotFound);

            await ExpectStatusAsync(
                $"{endpoint}/{Guid.Empty}",
                adminToken,
                HttpStatusCode.BadRequest);

            // 14. Disabled Admin must not use an existing JWT.
            await db.Users
                .Where(x => x.Id == admin.Id)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.IsActive, false));

            await ExpectStatusAsync(
                endpoint, adminToken,
                HttpStatusCode.Unauthorized);

            await ExpectStatusAsync(
                $"{endpoint}/{bobShipping.Id}",
                adminToken,
                HttpStatusCode.Unauthorized);

            // 15. Changing role invalidates access with old JWT.
            await db.Users
                .Where(x => x.Id == admin.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsActive, true)
                    .SetProperty(x => x.Role, UserRole.Customer));

            await ExpectStatusAsync(
                endpoint, adminToken,
                HttpStatusCode.Unauthorized);

            await ExpectStatusAsync(
                $"{endpoint}/{aliceNew.Id}",
                adminToken,
                HttpStatusCode.Unauthorized);

            // 16. Reads must not modify business data.
            Assert.Equal(4, await db.Orders.AsNoTracking()
                .CountAsync(x => orders
                    .Select(o => o.Id).Contains(x.Id)));

            Assert.Equal(10, await db.Products.AsNoTracking()
                .Where(x => x.Id == rose.Id)
                .Select(x => x.StockQuantity)
                .SingleAsync());

            Assert.Equal(999000m,
                await db.Products.AsNoTracking()
                    .Where(x => x.Id == rose.Id)
                    .Select(x => x.Price)
                    .SingleAsync());

            Assert.Equal(OrderStatus.Pending,
                (await db.Orders.AsNoTracking()
                    .SingleAsync(x => x.Id == aliceOld.Id)).Status);

            Assert.Equal(OrderStatus.Confirmed,
                (await db.Orders.AsNoTracking()
                    .SingleAsync(x => x.Id == aliceNew.Id)).Status);

            Assert.Equal(5, await db.Payments.AsNoTracking()
                .CountAsync(x => orders
                    .Select(o => o.Id).Contains(x.OrderId)));

            Assert.Empty(db.ChangeTracker.Entries<Order>());
        }
        finally
        {
            db.ChangeTracker.Clear();

            var orderIds = orders
                .Select(x => x.Id)
                .ToArray();

            await db.Payments
                .Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();

            await db.OrderItems
                .Where(x => orderIds.Contains(x.OrderId))
                .ExecuteDeleteAsync();

            await db.Orders
                .Where(x => orderIds.Contains(x.Id))
                .ExecuteDeleteAsync();

            await db.Products
                .Where(x => x.Id == rose.Id)
                .ExecuteDeleteAsync();

            await db.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x => x.Id == alice.Id ||
                            x.Id == bob.Id ||
                            x.Id == admin.Id)
                .ExecuteDeleteAsync();
        }
    }
}
