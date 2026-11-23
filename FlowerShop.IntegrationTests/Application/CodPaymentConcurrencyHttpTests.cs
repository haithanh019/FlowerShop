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

public sealed class CodPaymentConcurrencyHttpTests
{
    [Fact]
    public async Task CodPayment_ShouldRemainConsistentUnderConcurrency()
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
        var suffix = Guid.NewGuid().ToString("N");

        User NewAdmin(string label)
        {
            var user = new User
            {
                FullName = $"COD {label}",
                Email = $"cod-{label.ToLowerInvariant()}-{suffix}@example.invalid",
                Role = UserRole.Admin,
                IsActive = true
            };

            user.PasswordHash =
                hasher.HashPassword(user, password);

            return user;
        }

        var admin1 = NewAdmin("AdminOne");
        var admin2 = NewAdmin("AdminTwo");

        var customer = new User
        {
            FullName = "COD Concurrency Customer",
            Email = $"cod-customer-{suffix}@example.invalid",
            PasswordHash = "test-only-not-for-login",
            Role = UserRole.Customer,
            IsActive = true
        };

        var category = new Category
        {
            Name = "COD Concurrency Category",
            Slug = $"cod-concurrency-{suffix}"
        };

        var product = new Product
        {
            Name = "COD Concurrency Rose",
            Slug = $"cod-rose-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 10,
            IsActive = true
        };

        // Seed independent Order snapshots for each scenario.
        Order NewShippingOrder()
        {
            return new Order
            {
                UserId = customer.Id,
                OrderCode = $"FS-{Guid.NewGuid():N}",
                Status = OrderStatus.Shipping,

                RecipientName = "COD Customer",
                RecipientPhone = "0900000000",
                ShippingAddressLine = "1 Flower Street",
                ShippingWard = "Test Ward",
                ShippingDistrict = "Test District",
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
                        ProductId = product.Id,
                        ProductName = product.Name,
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
        }

        var duplicateOrder = NewShippingOrder();
        var raceOrder = NewShippingOrder();
        var invalidOrder = NewShippingOrder();

        var orderIds = new[]
        {
            duplicateOrder.Id,
            raceOrder.Id,
            invalidOrder.Id
        };

        db.AddRange(
            admin1, admin2, customer, category, product,
            duplicateOrder, raceOrder, invalidOrder);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        async Task<string> LoginAsync(User admin)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginDto
                {
                    Email = admin.Email,
                    Password = password
                });

            Assert.Equal(
                HttpStatusCode.OK,
                response.StatusCode);

            var data = await response.Content
                .ReadFromJsonAsync<LoginResponseDto>();

            return Assert.IsType<LoginResponseDto>(data)
                .AccessToken;
        }

        // Each concurrent request owns its authorization header.
        async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string url,
            string token,
            object? body = null)
        {
            using var request = new HttpRequestMessage(
                method, url);

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            if (body is not null)
                request.Content = JsonContent.Create(body);

            return await client.SendAsync(request);
        }

        static string ConfirmUrl(Guid orderId) =>
            $"/api/admin/orders/{orderId}/payments/cod/confirm";

        static string DeliveryUrl(Guid orderId) =>
            $"/api/admin/orders/{orderId}/status";

        async Task<Payment> GetPaymentAsync(Guid orderId) =>
            await db.Payments
                .AsNoTracking()
                .SingleAsync(x => x.OrderId == orderId);

        async Task<OrderStatus> GetOrderStatusAsync(Guid orderId) =>
            await db.Orders
                .AsNoTracking()
                .Where(x => x.Id == orderId)
                .Select(x => x.Status)
                .SingleAsync();

        try
        {
            var admin1Token = await LoginAsync(admin1);
            var admin2Token = await LoginAsync(admin2);

            // ==================================================
            // SCENARIO 1: Two Admins confirm the same COD payment.
            // Exactly one request may succeed.
            // ==================================================

            var confirmations = await Task.WhenAll(
                SendAsync(
                    HttpMethod.Post,
                    ConfirmUrl(duplicateOrder.Id),
                    admin1Token),
                SendAsync(
                    HttpMethod.Post,
                    ConfirmUrl(duplicateOrder.Id),
                    admin2Token));

            try
            {
                var success = Assert.Single(
                    confirmations,
                    x => x.StatusCode == HttpStatusCode.OK);

                var conflict = Assert.Single(
                    confirmations,
                    x => x.StatusCode == HttpStatusCode.Conflict);

                var result = await success.Content
                    .ReadFromJsonAsync<
                        CodPaymentSettlementResultDto>();

                Assert.Equal(
                    PaymentStatus.Paid,
                    Assert.IsType<
                        CodPaymentSettlementResultDto>(
                        result).Status);

                var problem = await conflict.Content
                    .ReadFromJsonAsync<ProblemDetails>();

                Assert.Equal(
                    "COD_PAYMENT_NOT_SETTLEABLE",
                    problem?.Detail);
            }
            finally
            {
                foreach (var response in confirmations)
                    response.Dispose();
            }

            var firstPayment = await GetPaymentAsync(
                duplicateOrder.Id);

            Assert.Equal(
                PaymentStatus.Paid,
                firstPayment.Status);

            Assert.NotNull(firstPayment.PaidAt);
            Assert.NotNull(firstPayment.UpdatedAt);

            Assert.Equal(
                OrderStatus.Shipping,
                await GetOrderStatusAsync(duplicateOrder.Id));

            // Repeated settlement must not overwrite PaidAt.
            using (var repeated = await SendAsync(
                HttpMethod.Post,
                ConfirmUrl(duplicateOrder.Id),
                admin1Token))
            {
                Assert.Equal(
                    HttpStatusCode.Conflict,
                    repeated.StatusCode);
            }

            var afterRetry = await GetPaymentAsync(
                duplicateOrder.Id);

            Assert.Equal(
                firstPayment.PaidAt,
                afterRetry.PaidAt);

            Assert.Equal(
                firstPayment.UpdatedAt,
                afterRetry.UpdatedAt);

            Assert.Equal(
                1,
                await db.Payments.AsNoTracking()
                    .CountAsync(x =>
                        x.OrderId == duplicateOrder.Id));

            // ==================================================
            // SCENARIO 2: COD settlement races with delivery.
            //
            // Settlement first: both requests may succeed.
            // Delivery first: delivery receives 409;
            // settlement succeeds afterward.
            // ==================================================

            var competing = await Task.WhenAll(
                SendAsync(
                    HttpMethod.Post,
                    ConfirmUrl(raceOrder.Id),
                    admin1Token),
                SendAsync(
                    HttpMethod.Patch,
                    DeliveryUrl(raceOrder.Id),
                    admin2Token,
                    new
                    {
                        status = (int)OrderStatus.Delivered
                    }));

            HttpStatusCode deliveryResult;

            try
            {
                Assert.Equal(
                    HttpStatusCode.OK,
                    competing[0].StatusCode);

                deliveryResult = competing[1].StatusCode;

                Assert.True(
                    deliveryResult is
                        HttpStatusCode.OK or
                        HttpStatusCode.Conflict,
                    $"Unexpected delivery status: {deliveryResult}");

                if (deliveryResult == HttpStatusCode.Conflict)
                {
                    var problem = await competing[1].Content
                        .ReadFromJsonAsync<ProblemDetails>();

                    Assert.Equal(
                        "COD_PAYMENT_NOT_SETTLED",
                        problem?.Detail);
                }
            }
            finally
            {
                foreach (var response in competing)
                    response.Dispose();
            }

            var racePayment = await GetPaymentAsync(
                raceOrder.Id);

            Assert.Equal(
                PaymentStatus.Paid,
                racePayment.Status);

            Assert.NotNull(racePayment.PaidAt);

            if (deliveryResult == HttpStatusCode.Conflict)
            {
                // Delivery executed before settlement.
                Assert.Equal(
                    OrderStatus.Shipping,
                    await GetOrderStatusAsync(raceOrder.Id));

                // Retry after settlement is now allowed.
                using var retry = await SendAsync(
                    HttpMethod.Patch,
                    DeliveryUrl(raceOrder.Id),
                    admin2Token,
                    new
                    {
                        status = (int)OrderStatus.Delivered
                    });

                Assert.Equal(
                    HttpStatusCode.OK,
                    retry.StatusCode);
            }

            // Regardless of request ordering, final state is valid.
            Assert.Equal(
                OrderStatus.Delivered,
                await GetOrderStatusAsync(raceOrder.Id));

            Assert.Equal(
                PaymentStatus.Paid,
                (await GetPaymentAsync(raceOrder.Id)).Status);

            // ==================================================
            // SCENARIO 3: Invalid financial snapshot.
            // Reject settlement without changing Payment.
            // ==================================================

            await db.OrderItems
                .Where(x => x.OrderId == invalidOrder.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.UnitPrice,
                    99000m));

            using (var invalidResponse = await SendAsync(
                HttpMethod.Post,
                ConfirmUrl(invalidOrder.Id),
                admin1Token))
            {
                Assert.Equal(
                    HttpStatusCode.Conflict,
                    invalidResponse.StatusCode);

                var problem = await invalidResponse.Content
                    .ReadFromJsonAsync<ProblemDetails>();

                Assert.Equal(
                    "INVALID_ORDER_ITEMS",
                    problem?.Detail);
            }

            var invalidPayment = await GetPaymentAsync(
                invalidOrder.Id);

            Assert.Equal(
                PaymentStatus.Pending,
                invalidPayment.Status);

            Assert.Null(invalidPayment.PaidAt);

            Assert.Equal(
                OrderStatus.Shipping,
                await GetOrderStatusAsync(invalidOrder.Id));

            // Verify COD settlement never changes stock.
            Assert.Equal(
                10,
                await db.Products.AsNoTracking()
                    .Where(x => x.Id == product.Id)
                    .Select(x => x.StockQuantity)
                    .SingleAsync());
        }
        finally
        {
            // Delete only data created by this test.
            db.ChangeTracker.Clear();

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
                .Where(x => x.Id == product.Id)
                .ExecuteDeleteAsync();

            await db.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x =>
                    x.Id == admin1.Id ||
                    x.Id == admin2.Id ||
                    x.Id == customer.Id)
                .ExecuteDeleteAsync();
        }
    }
}
