using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.Products;
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

public class ProductHttpTests
{
    [Fact]
    public async Task ProductCrud_And_OData_ShouldWork()
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

        var category = new Category
        {
            Name = "Test Product Category",
            Slug = $"test-category-{Guid.NewGuid():N}"
        };

        User NewUser(UserRole role)
        {
            var user = new User
            {
                FullName = "Product Test",
                Email = $"test-{Guid.NewGuid():N}@example.invalid",
                Role = role
            };

            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var admin = NewUser(UserRole.Admin);
        var customer = NewUser(UserRole.Customer);

        db.AddRange(category, admin, customer);
        await db.SaveChangesAsync();

        try
        {
            async Task<string> LoginAsync(User user)
            {
                var response = await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new LoginDto
                    {
                        Email = user.Email,
                        Password = password
                    });

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var result = await response.Content
                    .ReadFromJsonAsync<LoginResponseDto>();

                Assert.NotNull(result);
                return result.AccessToken;
            }

            // Authorization: Anonymous -> 401
            var anonymous = await client.GetAsync(
                "/odata/v1/AdminProducts");

            Assert.Equal(HttpStatusCode.Unauthorized,
                anonymous.StatusCode);

            // Authorization: Customer -> 403
            var customerToken = await LoginAsync(customer);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", customerToken);

            var forbidden = await client.GetAsync(
                "/odata/v1/AdminProducts");

            Assert.Equal(HttpStatusCode.Forbidden,
                forbidden.StatusCode);

            // Login as Admin
            var adminToken = await LoginAsync(admin);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", adminToken);

            // Create active Product
            var dto = new CreateProductDto
            {
                Name = "Hoa Hong",
                Slug = $"test-product-{Guid.NewGuid():N}",
                Price = 150000,
                StockQuantity = 5,
                CategoryId = category.Id,
                IsActive = true
            };

            var create = await client.PostAsJsonAsync(
                "/api/products", dto);

            Assert.Equal(HttpStatusCode.Created,
                create.StatusCode);

            var product = await create.Content
                .ReadFromJsonAsync<AdminProductODataDto>();

            Assert.NotNull(product);
            Assert.Equal(5, product.StockQuantity);

            // Duplicate Slug -> 409
            var duplicate = await client.PostAsJsonAsync(
                "/api/products", dto);

            Assert.Equal(HttpStatusCode.Conflict,
                duplicate.StatusCode);

            // Invalid request -> 400
            var invalid = await client.PostAsJsonAsync(
                "/api/products",
                new CreateProductDto
                {
                    Name = "",
                    Slug = $"test-invalid-{Guid.NewGuid():N}",
                    Price = 100000,
                    CategoryId = category.Id
                });

            Assert.Equal(HttpStatusCode.BadRequest,
                invalid.StatusCode);

            // Missing Category -> 404
            var missingCategory = await client.PostAsJsonAsync(
                "/api/products",
                new CreateProductDto
                {
                    Name = "Test",
                    Slug = $"test-missing-{Guid.NewGuid():N}",
                    Price = 100000,
                    CategoryId = Guid.NewGuid()
                });

            Assert.Equal(HttpStatusCode.NotFound,
                missingCategory.StatusCode);

            // Create hidden Product
            var hiddenRequest = await client.PostAsJsonAsync(
                "/api/products",
                new CreateProductDto
                {
                    Name = "Hoa An",
                    Slug = $"test-hidden-{Guid.NewGuid():N}",
                    Price = 90000,
                    StockQuantity = 0,
                    CategoryId = category.Id,
                    IsActive = false
                });

            Assert.Equal(HttpStatusCode.Created,
                hiddenRequest.StatusCode);

            var hidden = await hiddenRequest.Content
                .ReadFromJsonAsync<AdminProductODataDto>();

            Assert.NotNull(hidden);

            // Public OData: only active Products
            client.DefaultRequestHeaders.Authorization = null;

            var filter = Uri.EscapeDataString(
                $"CategoryId eq {category.Id}");

            var publicResponse = await client.GetAsync(
                $"/odata/v1/Products?$filter={filter}&$top=10");

            Assert.Equal(HttpStatusCode.OK,
                publicResponse.StatusCode);

            using var publicJson = JsonDocument.Parse(
                await publicResponse.Content.ReadAsStringAsync());

            var publicIds = publicJson.RootElement
                .GetProperty("value")
                .EnumerateArray()
                .Select(x => x.GetProperty("Id").GetGuid())
                .ToArray();

            Assert.Equal(new[] { product.Id }, publicIds);

            var hiddenPublic = await client.GetAsync(
                $"/odata/v1/Products({hidden.Id})");

            Assert.Equal(HttpStatusCode.NotFound,
                hiddenPublic.StatusCode);

            // Admin OData: active and hidden Products
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", adminToken);

            var adminResponse = await client.GetAsync(
                $"/odata/v1/AdminProducts?$filter={filter}&$orderby=Price%20desc&$top=10");

            Assert.Equal(HttpStatusCode.OK,
                adminResponse.StatusCode);

            using var adminJson = JsonDocument.Parse(
                await adminResponse.Content.ReadAsStringAsync());

            var adminIds = adminJson.RootElement
                .GetProperty("value")
                .EnumerateArray()
                .Select(x => x.GetProperty("Id").GetGuid())
                .ToArray();

            Assert.Equal(new[] { product.Id, hidden.Id }, adminIds);

            // Update Product without changing StockQuantity
            var update = await client.PutAsJsonAsync(
                $"/api/products/{product.Id}",
                new UpdateProductDto
                {
                    Name = "Hoa Hong Updated",
                    Slug = dto.Slug,
                    Price = 160000,
                    CategoryId = category.Id,
                    IsActive = true
                });

            Assert.Equal(HttpStatusCode.OK,
                update.StatusCode);

            var updated = await update.Content
                .ReadFromJsonAsync<AdminProductODataDto>();

            Assert.NotNull(updated);
            Assert.Equal(160000m, updated.Price);
            Assert.Equal(5, updated.StockQuantity);

            // Delete = hide Product, not physical delete
            var delete = await client.DeleteAsync(
                $"/api/products/{product.Id}");

            Assert.Equal(HttpStatusCode.NoContent,
                delete.StatusCode);

            var publicAfterDelete = await client.GetAsync(
                $"/odata/v1/Products({product.Id})");

            Assert.Equal(HttpStatusCode.NotFound,
                publicAfterDelete.StatusCode);

            var stored = await db.Products
                .AsNoTracking()
                .SingleAsync(x => x.Id == product.Id);

            Assert.False(stored.IsActive);
            Assert.Equal(5, stored.StockQuantity);
        }
        finally
        {
            // Delete only records belonging to this test
            await db.Products
                .Where(x => x.CategoryId == category.Id)
                .ExecuteDeleteAsync();

            db.Categories.Remove(category);
            db.Users.RemoveRange(admin, customer);

            await db.SaveChangesAsync();
        }
    }
}
