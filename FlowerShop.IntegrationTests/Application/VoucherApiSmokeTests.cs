using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.Vouchers;
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

public sealed class VoucherApiSmokeTests
{
    private const string AdminRoute = "/api/admin/vouchers";
    private const string AvailableRoute = "/api/vouchers/available";
    private const string PreviewRoute = "/api/vouchers/preview";
    private const string Password = "FlowerShop#2026";

    [Fact]
    public async Task VoucherApi_ShouldExposeSevenRoutesWithRoleProtectionAndSafePreview()
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var tag = $"API-{Guid.NewGuid():N}";
        var code = tag.ToUpperInvariant();
        User NewUser(UserRole role)
        {
            var user = new User { FullName = "Voucher API Test", Email = $"{Guid.NewGuid():N}@example.invalid", Role = role };
            user.PasswordHash = hasher.HashPassword(user, Password);
            return user;
        }
        var admin = NewUser(UserRole.Admin);
        var customer = NewUser(UserRole.Customer);
        var category = new Category { Name = "Voucher API Category", Slug = tag.ToLowerInvariant() };
        var product = new Product { CategoryId = category.Id, Name = "Rose", Slug = tag.ToLowerInvariant(), Price = 500000m, StockQuantity = 10, IsActive = true };
        var cart = new Cart { UserId = customer.Id };
        var item = new CartItem { CartId = cart.Id, ProductId = product.Id, Quantity = 1 };
        var now = DateTimeOffset.UtcNow;
        var request = new CreateVoucherDto
        {
            Code = $" {tag.ToLowerInvariant()} ", DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, MinOrderAmount = 300000m, UsageLimit = 10, PerUserLimit = 1,
            StartAt = now.AddDays(-1), EndAt = now.AddDays(1), IsActive = true
        };
        try
        {
            db.AddRange(admin, customer, category, product, cart, item);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            using var anonymous = CreateClient(factory);
            using var adminClient = CreateClient(factory);
            using var customerClient = CreateClient(factory);
            using (var response = await anonymous.GetAsync(AdminRoute))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using (var response = await anonymous.GetAsync(AvailableRoute))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await LoginAsync(adminClient, admin.Email);
            await LoginAsync(customerClient, customer.Email);
            using (var response = await customerClient.GetAsync(AdminRoute))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await adminClient.PostAsJsonAsync(PreviewRoute, new VoucherPreviewRequestDto { VoucherCode = code }))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using var create = await adminClient.PostAsJsonAsync(AdminRoute, request);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = Assert.IsType<VoucherDto>(await create.Content.ReadFromJsonAsync<VoucherDto>());
            Assert.Equal(code, created.Code);
            Assert.Equal(0, created.UsedCount);
            Assert.NotNull(create.Headers.Location);
            var detailPath = $"{AdminRoute}/{created.VoucherId}";
            Assert.EndsWith(detailPath, create.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

            using (var list = await adminClient.GetAsync($"{AdminRoute}?codeKeyword={code}&pageSize=1"))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                var page = Assert.IsType<AdminVoucherPageDto>(await list.Content.ReadFromJsonAsync<AdminVoucherPageDto>());
                Assert.Equal(created.VoucherId, Assert.Single(page.Items).Voucher.VoucherId);
            }
            using (var detail = await adminClient.GetAsync(detailPath))
            {
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                var view = Assert.IsType<AdminVoucherViewDto>(await detail.Content.ReadFromJsonAsync<AdminVoucherViewDto>());
                Assert.Equal(VoucherAvailabilityStatus.Available, view.Status);
            }
            var update = new UpdateVoucherDto
            {
                Description = "Updated", DiscountType = request.DiscountType, DiscountValue = 25m,
                MinOrderAmount = request.MinOrderAmount, UsageLimit = request.UsageLimit, PerUserLimit = request.PerUserLimit,
                StartAt = request.StartAt, EndAt = request.EndAt
            };
            using (var put = await adminClient.PutAsJsonAsync(detailPath, update))
            {
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
                Assert.Equal(25m, Assert.IsType<VoucherDto>(await put.Content.ReadFromJsonAsync<VoucherDto>()).DiscountValue);
            }
            using (var badStatus = await adminClient.PatchAsJsonAsync(detailPath + "/status", new { }))
                Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
            using (var off = await adminClient.PatchAsJsonAsync(detailPath + "/status", new UpdateVoucherStatusDto { IsActive = false }))
            {
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
                Assert.False(Assert.IsType<VoucherDto>(await off.Content.ReadFromJsonAsync<VoucherDto>()).IsActive);
            }
            using (var denied = await customerClient.PostAsJsonAsync(PreviewRoute, new VoucherPreviewRequestDto { VoucherCode = code }))
                Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            using (var on = await adminClient.PatchAsJsonAsync(detailPath + "/status", new UpdateVoucherStatusDto { IsActive = true }))
                Assert.Equal(HttpStatusCode.OK, on.StatusCode);

            // Search all pages so pre-existing fixtures do not make the test depend on database emptiness.
            JsonElement? found = null;
            int totalPages;
            var pageNumber = 1;
            do
            {
                using var listing = await customerClient.GetAsync($"{AvailableRoute}?page={pageNumber}&pageSize=50&userId={admin.Id}");
                Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
                Assert.True(listing.Headers.CacheControl?.NoStore == true);
                using var json = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
                totalPages = json.RootElement.GetProperty("totalPages").GetInt32();
                foreach (var row in json.RootElement.GetProperty("items").EnumerateArray())
                    if (row.GetProperty("code").GetString() == code) found = row.Clone();
                pageNumber++;
            } while (found is null && pageNumber <= totalPages);
            Assert.True(found.HasValue);
            var publicFields = found.GetValueOrDefault().EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] expected = ["code", "description", "discountType", "discountValue", "endAtUtc", "maxDiscountAmount", "minOrderAmount", "perUserLimit", "startAtUtc"];
            Assert.Equal(expected, publicFields);

            using var preview = await customerClient.PostAsJsonAsync(PreviewRoute,
                new { voucherCode = code.ToLowerInvariant(), subtotal = 1, discountAmount = 500000, userId = admin.Id });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.True(preview.Headers.CacheControl?.NoStore == true);
            var quote = Assert.IsType<VoucherPreviewResultDto>(await preview.Content.ReadFromJsonAsync<VoucherPreviewResultDto>());
            Assert.Equal(500000m, quote.Subtotal);
            Assert.Equal(125000m, quote.DiscountAmount);
            Assert.Equal(375000m, quote.TotalAmount);
            Assert.Equal(0, (await db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == created.VoucherId)).UsedCount);
            Assert.Equal(10, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == product.Id)).StockQuantity);
            Assert.Equal(1, (await db.CartItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).Quantity);
            Assert.False(await db.Orders.AnyAsync(x => x.UserId == customer.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.CartItems.Where(x => x.CartId == cart.Id).ExecuteDeleteAsync();
            await db.Carts.Where(x => x.Id == cart.Id).ExecuteDeleteAsync();
            await db.Products.Where(x => x.Id == product.Id).ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id).ExecuteDeleteAsync();
            await db.Vouchers.Where(x => x.Code == code).ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == admin.Id || x.Id == customer.Id).ExecuteDeleteAsync();
        }
    }

    private static HttpClient CreateClient(TestApiFactory factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = Assert.IsType<LoginResponseDto>(await response.Content.ReadFromJsonAsync<LoginResponseDto>());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
    }
}
