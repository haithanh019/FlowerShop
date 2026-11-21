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

public sealed class OrderManagementHttpTests
{
    [Fact]
    public async Task OrderManagementHttp_ShouldAuthorizeTransitionCancelAndRestoreStockExactlyOnce()
    {
        // Never point this integration test at the production database.
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
            m => m.EndsWith("_InitialCreate"));

        const string password = "TestPassword#2026";
        var suffix = Guid.NewGuid().ToString("N");

        User NewUser(string label, UserRole role)
        {
            var user = new User
            {
                FullName = $"Order Management {label}",
                Email = $"manage-{label.ToLowerInvariant()}-{suffix}@example.invalid",
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
            Name = "Order Management HTTP Test",
            Slug = $"manage-category-{suffix}"
        };

        // Sorting by ProductId ensures the first stock increase succeeds,
        // then the overflow fails in the deliberate rollback scenario.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(x => x)
            .ToArray();

        var rose = new Product
        {
            Id = ids[0],
            Name = "Management Rose",
            Slug = $"manage-rose-{suffix}",
            CategoryId = category.Id,
            Price = 100000m,
            StockQuantity = 5,
            IsActive = true
        };
        var tulip = new Product
        {
            Id = ids[1],
            Name = "Discontinued Tulip",
            Slug = $"manage-tulip-{suffix}",
            CategoryId = category.Id,
            Price = 80000m,
            StockQuantity = 0,
            IsActive = false // Restocking must work for inactive products.
        };
        var overflowProduct = new Product
        {
            Id = ids[2],
            Name = "Overflow Product",
            Slug = $"manage-overflow-{suffix}",
            CategoryId = category.Id,
            Price = 10000m,
            StockQuantity = int.MaxValue,
            IsActive = true
        };

        Order NewOrder(
            User owner,
            OrderStatus status,
            PaymentStatus paymentStatus,
            params (Product Product, int Quantity)[] lines)
        {
            var items = lines.Select(line => new OrderItem
            {
                ProductId = line.Product.Id,
                ProductName = line.Product.Name,
                UnitPrice = line.Product.Price,
                Quantity = line.Quantity
            }).ToList();

            var amount = items.Sum(x => x.UnitPrice * x.Quantity);
            return new Order
            {
                UserId = owner.Id,
                OrderCode = $"FS-{Guid.NewGuid():N}",
                Status = status,
                RecipientName = "Test Recipient",
                RecipientPhone = "0900000000",
                ShippingAddressLine = "1 Flower Street",
                ShippingWard = "Test Ward",
                ShippingDistrict = "Test District",
                ShippingProvince = "Da Nang",
                ShippingCountryCode = "VN",
                Subtotal = amount,
                ShippingFee = 0m,
                DiscountAmount = 0m,
                TotalAmount = amount,
                Items = items,
                Payments =
                [
                    new Payment
                    {
                        Method = PaymentMethod.CashOnDelivery,
                        Status = paymentStatus,
                        Amount = amount,
                        Currency = "VND"
                    }
                ]
            };
        }

        var progress = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 1));
        var customerCancel = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 2), (tulip, 1));
        var adminCancel = NewOrder(alice, OrderStatus.Confirmed,
            PaymentStatus.Pending, (rose, 1));
        var paid = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Paid, (rose, 1));
        var rollback = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 1), (overflowProduct, 1));
        var bobPrivate = NewOrder(bob, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 1));
        var simultaneousCancel = NewOrder(bob, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 1));
        var cancellationVsProgress = NewOrder(alice, OrderStatus.Pending,
            PaymentStatus.Pending, (rose, 1));

        var orderIds = new[]
        {
            progress.Id, customerCancel.Id, adminCancel.Id, paid.Id,
            rollback.Id, bobPrivate.Id, simultaneousCancel.Id,
            cancellationVsProgress.Id
        };

        db.AddRange(alice, bob, admin, category, rose, tulip,
            overflowProduct, progress, customerCancel, adminCancel,
            paid, rollback, bobPrivate, simultaneousCancel,
            cancellationVsProgress);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        const string adminBase = "/api/admin/orders";
        const string customerBase = "/api/orders";

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

        // Authorization is assigned per request, not through shared
        // DefaultRequestHeaders, so parallel calls cannot swap JWTs.
        async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string url,
            string? bearer,
            object? body = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (bearer is not null)
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", bearer);
            }
            if (body is not null)
                request.Content = JsonContent.Create(body);

            return await client.SendAsync(request);
        }

        async Task ExpectStatusAsync(
            HttpMethod method,
            string url,
            string? token,
            HttpStatusCode expected,
            object? body = null)
        {
            using var response = await SendAsync(method, url, token, body);
            Assert.Equal(expected, response.StatusCode);
        }

        async Task<OrderStatusUpdateResultDto> ExpectResultAsync(
            HttpMethod method,
            string url,
            string? token,
            OrderStatus previous,
            OrderStatus next,
            object? body = null)
        {
            using var response = await SendAsync(method, url, token, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var value = await response.Content
                .ReadFromJsonAsync<OrderStatusUpdateResultDto>();
            var result = Assert.IsType<OrderStatusUpdateResultDto>(value);
            Assert.Equal(previous, result.PreviousStatus);
            Assert.Equal(next, result.Status);
            Assert.Equal(DateTimeKind.Utc, result.UpdatedAtUtc.Kind);
            return result;
        }

        async Task<int> StockAsync(Guid id) =>
            await db.Products.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => x.StockQuantity)
                .SingleAsync();

        async Task<OrderStatus> OrderStatusAsync(Guid id) =>
            await db.Orders.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => x.Status)
                .SingleAsync();

        async Task<PaymentStatus> PaymentStatusAsync(Guid id) =>
            await db.Payments.AsNoTracking()
                .Where(x => x.OrderId == id)
                .Select(x => x.Status)
                .SingleAsync();

        try
        {
            var aliceToken = await LoginAsync(alice);
            var bobToken = await LoginAsync(bob);
            var adminToken = await LoginAsync(admin);

            string StatusUrl(Guid orderId) =>
                $"{adminBase}/{orderId}/status";
            string AdminCancelUrl(Guid orderId) =>
                $"{adminBase}/{orderId}/cancel";
            string CustomerCancelUrl(Guid orderId) =>
                $"{customerBase}/{orderId}/cancel";
            string ConfirmCodUrl(Guid orderId) =>
                $"{adminBase}/{orderId}/payments/cod/confirm";

            // 1. Authentication and role authorization.
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(progress.Id), null,
                HttpStatusCode.Unauthorized,
                new { status = 1 });
            await ExpectStatusAsync(HttpMethod.Post,
                AdminCancelUrl(adminCancel.Id), null,
                HttpStatusCode.Unauthorized);
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(customerCancel.Id), null,
                HttpStatusCode.Unauthorized);
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(progress.Id), aliceToken,
                HttpStatusCode.Forbidden,
                new { status = 1 });
            await ExpectStatusAsync(HttpMethod.Post,
                AdminCancelUrl(adminCancel.Id), aliceToken,
                HttpStatusCode.Forbidden);
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(customerCancel.Id), adminToken,
                HttpStatusCode.Forbidden);
            // COD confirmation requires authenticated Admin.
            await ExpectStatusAsync(
                HttpMethod.Post,
                ConfirmCodUrl(progress.Id),
                null,
                HttpStatusCode.Unauthorized);

            await ExpectStatusAsync(
                HttpMethod.Post,
                ConfirmCodUrl(progress.Id),
                aliceToken,
                HttpStatusCode.Forbidden);

            // 2. Model validation and non-existent order.
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(progress.Id), adminToken,
                HttpStatusCode.BadRequest, new Dictionary<string, object?>());
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(progress.Id), adminToken,
                HttpStatusCode.BadRequest, new { status = 999 });
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(Guid.Empty), adminToken,
                HttpStatusCode.BadRequest, new { status = 1 });
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(Guid.Empty), aliceToken,
                HttpStatusCode.BadRequest);
            await ExpectStatusAsync(HttpMethod.Post,
                AdminCancelUrl(Guid.Empty), adminToken,
                HttpStatusCode.BadRequest);
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(Guid.NewGuid()), adminToken,
                HttpStatusCode.NotFound, new { status = 1 });
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(Guid.NewGuid()), aliceToken,
                HttpStatusCode.NotFound);

            // 3. Customer cannot cancel another customer's order.
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(bobPrivate.Id), aliceToken,
                HttpStatusCode.NotFound);
            Assert.Equal(OrderStatus.Pending,
                await OrderStatusAsync(bobPrivate.Id));

            // 4. Admin cannot skip states or use the status endpoint
            // to bypass the cancellation/restocking workflow.
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(progress.Id), adminToken,
                HttpStatusCode.Conflict, new { status = 3 });
            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(progress.Id), adminToken,
                HttpStatusCode.Conflict, new { status = 5 });
            Assert.Equal(OrderStatus.Pending,
                await OrderStatusAsync(progress.Id));

            // 5. All four valid Admin transitions, verified in SQL.
            var states = new[]
            {
                OrderStatus.Confirmed,
                OrderStatus.Preparing,
                OrderStatus.Shipping,
                OrderStatus.Delivered
            };
            var previous = OrderStatus.Pending;
            foreach (var next in states)
            {


                if (next == OrderStatus.Delivered)
                {
                    // Delivery without COD collection must fail.
                    await ExpectStatusAsync(
                        HttpMethod.Patch,
                        StatusUrl(progress.Id),
                        adminToken,
                        HttpStatusCode.Conflict,
                        new
                        {
                            status = (int)OrderStatus.Delivered
                        });

                    Assert.Equal(
                        OrderStatus.Shipping,
                        await OrderStatusAsync(progress.Id));

                    // Admin confirms COD collection.
                    using var settlementResponse = await SendAsync(
                        HttpMethod.Post,
                        ConfirmCodUrl(progress.Id),
                        adminToken);

                    Assert.Equal(
                        HttpStatusCode.OK,
                        settlementResponse.StatusCode);

                    var settlement = await settlementResponse.Content
                        .ReadFromJsonAsync<
                            CodPaymentSettlementResultDto>();

                    var paymentResult =
                        Assert.IsType<
                            CodPaymentSettlementResultDto>(
                            settlement);

                    Assert.Equal(
                        progress.Id,
                        paymentResult.OrderId);

                    Assert.Equal(
                        PaymentStatus.Paid,
                        paymentResult.Status);

                    Assert.Equal(
                        PaymentStatus.Paid,
                        await PaymentStatusAsync(progress.Id));

                    // A second confirmation cannot settle twice.
                    await ExpectStatusAsync(
                        HttpMethod.Post,
                        ConfirmCodUrl(progress.Id),
                        adminToken,
                        HttpStatusCode.Conflict);
                }



                var result = await ExpectResultAsync(
                    HttpMethod.Patch, StatusUrl(progress.Id), adminToken,
                    previous, next, new { status = (int)next });
                Assert.Equal(progress.Id, result.OrderId);
                Assert.Equal(progress.OrderCode, result.OrderCode);
                Assert.Equal(next, await OrderStatusAsync(progress.Id));
                previous = next;
            }

            await ExpectStatusAsync(HttpMethod.Patch,
                StatusUrl(progress.Id), adminToken,
                HttpStatusCode.Conflict, new { status = 4 });
            await ExpectStatusAsync(HttpMethod.Post,
                AdminCancelUrl(progress.Id), adminToken,
                HttpStatusCode.Conflict);

            Assert.Equal(
                PaymentStatus.Paid,
                await PaymentStatusAsync(progress.Id));

            Assert.NotNull(
                await db.Payments.AsNoTracking()
                    .Where(x => x.OrderId == progress.Id)
                    .Select(x => x.PaidAt)
                    .SingleAsync());

            Assert.Equal(5, await StockAsync(rose.Id));

            // 6. Paid COD cannot be cancelled automatically.
            await ExpectStatusAsync(HttpMethod.Post,
                AdminCancelUrl(paid.Id), adminToken,
                HttpStatusCode.Conflict);
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(paid.Id), aliceToken,
                HttpStatusCode.Conflict);
            Assert.Equal(OrderStatus.Pending,
                await OrderStatusAsync(paid.Id));
            Assert.Equal(PaymentStatus.Paid,
                await PaymentStatusAsync(paid.Id));
            Assert.Equal(5, await StockAsync(rose.Id));

            // 7. Customer cancellation restores both products,
            // including a Product whose IsActive flag is false.
            var cancelled = await ExpectResultAsync(
                HttpMethod.Post, CustomerCancelUrl(customerCancel.Id),
                aliceToken, OrderStatus.Pending, OrderStatus.Cancelled);
            Assert.Equal(customerCancel.Id, cancelled.OrderId);
            Assert.Equal(OrderStatus.Cancelled,
                await OrderStatusAsync(customerCancel.Id));
            Assert.Equal(PaymentStatus.Cancelled,
                await PaymentStatusAsync(customerCancel.Id));
            Assert.Equal(7, await StockAsync(rose.Id));
            Assert.Equal(1, await StockAsync(tulip.Id));

            // Duplicate cancellation must not restore stock twice.
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(customerCancel.Id), aliceToken,
                HttpStatusCode.Conflict);
            await ExpectStatusAsync(HttpMethod.Post,
                AdminCancelUrl(customerCancel.Id), adminToken,
                HttpStatusCode.Conflict);
            Assert.Equal(7, await StockAsync(rose.Id));
            Assert.Equal(1, await StockAsync(tulip.Id));

            // 8. Only Admin can cancel a Confirmed Order.
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(adminCancel.Id), aliceToken,
                HttpStatusCode.Conflict);
            await ExpectResultAsync(
                HttpMethod.Post, AdminCancelUrl(adminCancel.Id),
                adminToken, OrderStatus.Confirmed, OrderStatus.Cancelled);
            Assert.Equal(PaymentStatus.Cancelled,
                await PaymentStatusAsync(adminCancel.Id));
            Assert.Equal(8, await StockAsync(rose.Id));

            // 9. Force overflow on the second product. First increase
            // must be rolled back, along with Order/Payment changes.
            await ExpectStatusAsync(HttpMethod.Post,
                CustomerCancelUrl(rollback.Id), aliceToken,
                HttpStatusCode.Conflict);
            Assert.Equal(8, await StockAsync(rose.Id));
            Assert.Equal(int.MaxValue,
                await StockAsync(overflowProduct.Id));
            Assert.Equal(OrderStatus.Pending,
                await OrderStatusAsync(rollback.Id));
            Assert.Equal(PaymentStatus.Pending,
                await PaymentStatusAsync(rollback.Id));

            // 10. Two HTTP cancellations run concurrently with
            // independent credentials. Exactly one can succeed.
            var attempts = await Task.WhenAll(
                SendAsync(HttpMethod.Post,
                    CustomerCancelUrl(simultaneousCancel.Id), bobToken),
                SendAsync(HttpMethod.Post,
                    AdminCancelUrl(simultaneousCancel.Id), adminToken));

            try
            {
                Assert.Equal(1, attempts.Count(x =>
                    x.StatusCode == HttpStatusCode.OK));
                Assert.Equal(1, attempts.Count(x =>
                    x.StatusCode == HttpStatusCode.Conflict));
            }
            finally
            {
                foreach (var response in attempts)
                    response.Dispose();
            }
            Assert.Equal(OrderStatus.Cancelled,
                await OrderStatusAsync(simultaneousCancel.Id));
            Assert.Equal(PaymentStatus.Cancelled,
                await PaymentStatusAsync(simultaneousCancel.Id));
            Assert.Equal(9, await StockAsync(rose.Id));

            // 11. Race between status change and cancellation.
            // Whichever operation wins the OrderId lock determines
            // the valid final state; the losing operation gets 409.
            var competing = await Task.WhenAll(
                SendAsync(HttpMethod.Patch,
                    StatusUrl(cancellationVsProgress.Id), adminToken,
                    new { status = (int)OrderStatus.Confirmed }),
                SendAsync(HttpMethod.Post,
                    CustomerCancelUrl(cancellationVsProgress.Id), aliceToken));

            try
            {
                Assert.Equal(1, competing.Count(x =>
                    x.StatusCode == HttpStatusCode.OK));
                Assert.Equal(1, competing.Count(x =>
                    x.StatusCode == HttpStatusCode.Conflict));
            }
            finally
            {
                foreach (var response in competing)
                    response.Dispose();
            }

            var final = await OrderStatusAsync(cancellationVsProgress.Id);
            var finalPayment = await PaymentStatusAsync(
                cancellationVsProgress.Id);
            Assert.True(final is OrderStatus.Confirmed or OrderStatus.Cancelled);
            if (final == OrderStatus.Cancelled)
            {
                Assert.Equal(PaymentStatus.Cancelled, finalPayment);
                Assert.Equal(10, await StockAsync(rose.Id));
            }
            else
            {
                Assert.Equal(PaymentStatus.Pending, finalPayment);
                Assert.Equal(9, await StockAsync(rose.Id));
            }

            // The unrelated order remains untouched throughout.
            Assert.Equal(OrderStatus.Pending,
                await OrderStatusAsync(bobPrivate.Id));
            Assert.Equal(PaymentStatus.Pending,
                await PaymentStatusAsync(bobPrivate.Id));
        }
        finally
        {
            // Delete only data created in this test, in FK order.
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
                .Where(x => x.Id == rose.Id ||
                            x.Id == tulip.Id ||
                            x.Id == overflowProduct.Id)
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
