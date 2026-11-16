using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.ProductImages;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public class ProductImageHttpTests
{
    // Valid 1x1 PNG image for testing
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7+YewAAAAASUVORK5CYII=");

    [Fact]
    public async Task ProductImageManagement_ShouldWork()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing test connection.");

        var storage = new FakeImageStorageRepository();

        using var baseFactory = new TestApiFactory(connection);

        // Replace Cloudinary with Fake Storage in the test API
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IImageStorageRepository>();
                services.AddSingleton<IImageStorageRepository>(storage);
            }));

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

        User NewUser(UserRole role)
        {
            var user = new User
            {
                FullName = "Image Integration Test",
                Email = $"test-{Guid.NewGuid():N}@example.invalid",
                Role = role,
                IsActive = true
            };

            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var admin = NewUser(UserRole.Admin);
        var customer = NewUser(UserRole.Customer);

        var category = new Category
        {
            Name = "Image Test Category",
            Slug = $"image-category-{Guid.NewGuid():N}"
        };

        var product = new Product
        {
            Name = "Image Test Product",
            Slug = $"image-product-{Guid.NewGuid():N}",
            Price = 100000,
            StockQuantity = 5,
            CategoryId = category.Id,
            IsActive = true
        };

        db.AddRange(admin, customer, category, product);
        await db.SaveChangesAsync();

        var url = $"/api/products/{product.Id}/images";
        var adminImagesUrl = $"/api/admin/products/{product.Id}/images";
        client.DefaultRequestHeaders.Authorization = null;

        using (var response = await client.GetAsync(adminImagesUrl))
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                response.StatusCode);
        }

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

                var result = await response.Content
                    .ReadFromJsonAsync<LoginResponseDto>();

                return Assert.IsType<LoginResponseDto>(result)
                    .AccessToken;
            }

            // 1. Anonymous cannot upload
            using (var form = CreateImageForm(PngBytes))
            using (var response = await client.PostAsync(url, form))
            {
                Assert.Equal(HttpStatusCode.Unauthorized,
                    response.StatusCode);
            }

            // 2. Customer cannot upload
            var customerToken = await LoginAsync(customer);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", customerToken);

            using (var form = CreateImageForm(PngBytes))
            using (var response = await client.PostAsync(url, form))
            {
                Assert.Equal(HttpStatusCode.Forbidden,
                    response.StatusCode);
            }

            // 3. Login as Admin
            var adminToken = await LoginAsync(admin);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", adminToken);

            async Task<ProductImageDto> UploadAsync()
            {
                using var form = CreateImageForm(PngBytes);
                using var response = await client.PostAsync(url, form);

                Assert.Equal(HttpStatusCode.Created,
                    response.StatusCode);

                var result = await response.Content
                    .ReadFromJsonAsync<ProductImageDto>();

                return Assert.IsType<ProductImageDto>(result);
            }

            // 4. Upload two images
            var first = await UploadAsync();
            var second = await UploadAsync();

            // Public can view active Product images
            client.DefaultRequestHeaders.Authorization = null;

            var publicImages = await client
                .GetFromJsonAsync<List<ProductImageDto>>(url);

            Assert.NotNull(publicImages);
            Assert.Equal(2, publicImages.Count);

            Assert.Contains(publicImages, x => x.Id == first.Id);
            Assert.Contains(publicImages, x => x.Id == second.Id);

            // Restore Admin token
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", adminToken);

            // Admin can view images
            var adminImages = await client
                .GetFromJsonAsync<List<ProductImageDto>>(adminImagesUrl);

            Assert.NotNull(adminImages);
            Assert.Equal(2, adminImages.Count);

            // Hide Product
            product.IsActive = false;
            await db.SaveChangesAsync();

            // Public must not view hidden Product images
            using (var response = await client.GetAsync(url))
            {
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    response.StatusCode);
            }

            // Admin can still view hidden Product images
            adminImages = await client
                .GetFromJsonAsync<List<ProductImageDto>>(adminImagesUrl);

            Assert.NotNull(adminImages);
            Assert.Equal(2, adminImages.Count);

            // Restore Product state for remaining tests
            product.IsActive = true;
            await db.SaveChangesAsync();

            Assert.NotEqual(first.Id, second.Id);

            var imageCount = await db.ProductImages
                .AsNoTracking()
                .CountAsync(x => x.ProductId == product.Id);

            Assert.Equal(2, imageCount);
            Assert.Equal(2, storage.UploadedIds.Count);

            // 5. Invalid image signature -> 400
            using (var form = CreateImageForm(
                "Not a real image"u8.ToArray()))
            using (var response = await client.PostAsync(url, form))
            {
                Assert.Equal(HttpStatusCode.BadRequest,
                    response.StatusCode);
            }

            Assert.Equal(2, storage.UploadedIds.Count);

            // 6. Missing Product -> 404
            using (var form = CreateImageForm(PngBytes))
            using (var response = await client.PostAsync(
                $"/api/products/{Guid.NewGuid()}/images", form))
            {
                Assert.Equal(HttpStatusCode.NotFound,
                    response.StatusCode);
            }

            // 7. Set first image as primary
            using (var response = await client.PutAsync(
                $"{url}/{first.Id}/primary", null))
            {
                Assert.Equal(HttpStatusCode.NoContent,
                    response.StatusCode);
            }

            // 8. Change primary image to second
            using (var response = await client.PutAsync(
                $"{url}/{second.Id}/primary", null))
            {
                Assert.Equal(HttpStatusCode.NoContent,
                    response.StatusCode);
            }

            var primaryImages = await db.ProductImages
                .AsNoTracking()
                .Where(x => x.ProductId == product.Id && x.IsPrimary)
                .ToListAsync();

            Assert.Single(primaryImages);
            Assert.Equal(second.Id, primaryImages[0].Id);

            // 9. Reorder images
            using (var response = await client.PutAsJsonAsync(
                $"{url}/order",
                new ReorderProductImagesDto
                {
                    ImageIds = [second.Id, first.Id]
                }))
            {
                Assert.Equal(HttpStatusCode.NoContent,
                    response.StatusCode);
            }

            var orderedIds = await db.ProductImages
                .AsNoTracking()
                .Where(x => x.ProductId == product.Id)
                .OrderBy(x => x.SortOrder)
                .Select(x => x.Id)
                .ToArrayAsync();

            Assert.Equal([second.Id, first.Id], orderedIds);

            var sortedImages = await client
                .GetFromJsonAsync<List<ProductImageDto>>(url);

            Assert.NotNull(sortedImages);

            Assert.Equal(
                [second.Id, first.Id],
                [.. sortedImages.Select(x => x.Id)]);

            Assert.Equal(
                [0, 1],
                [.. sortedImages.Select(x => x.SortOrder)]);

            // 10. Duplicate IDs -> 400
            using (var response = await client.PutAsJsonAsync(
                $"{url}/order",
                new ReorderProductImagesDto
                {
                    ImageIds = [first.Id, first.Id]
                }))
            {
                Assert.Equal(HttpStatusCode.BadRequest,
                    response.StatusCode);
            }

            // 11. Missing an image ID -> 400
            using (var response = await client.PutAsJsonAsync(
                $"{url}/order",
                new ReorderProductImagesDto
                {
                    ImageIds = [first.Id]
                }))
            {
                Assert.Equal(HttpStatusCode.BadRequest,
                    response.StatusCode);
            }

            // 12. Cloudinary failure must not delete SQL record
            storage.FailDeletion = true;

            using (var response = await client.DeleteAsync(
                $"{url}/{first.Id}"))
            {
                Assert.Equal(HttpStatusCode.InternalServerError,
                    response.StatusCode);
            }

            Assert.True(await db.ProductImages
                .AsNoTracking()
                .AnyAsync(x => x.Id == first.Id));

            // 13. Successful deletion
            storage.FailDeletion = false;

            using (var response = await client.DeleteAsync(
                $"{url}/{first.Id}"))
            {
                Assert.Equal(HttpStatusCode.NoContent,
                    response.StatusCode);
            }

            Assert.False(await db.ProductImages
                .AsNoTracking()
                .AnyAsync(x => x.Id == first.Id));

            Assert.Single(storage.DeletedIds);

            // The remaining primary image is unaffected
            var remaining = await db.ProductImages
                .AsNoTracking()
                .SingleAsync(x => x.Id == second.Id);

            Assert.True(remaining.IsPrimary);


            // 14. Anonymous cannot access admin endpoint
            client.DefaultRequestHeaders.Authorization = null;

            using (var response = await client.GetAsync(adminImagesUrl))
            {
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    response.StatusCode);
            }

            // 15. Customer cannot access admin endpoint
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", customerToken);

            using (var response = await client.GetAsync(adminImagesUrl))
            {
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    response.StatusCode);
            }
        }
        finally
        {
            // Clean up only this test's records
            await db.ProductImages
                .Where(x => x.ProductId == product.Id)
                .ExecuteDeleteAsync();

            await db.Products
                .Where(x => x.Id == product.Id)
                .ExecuteDeleteAsync();

            await db.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x => x.Id == admin.Id || x.Id == customer.Id)
                .ExecuteDeleteAsync();
        }
    }

    private static MultipartFormDataContent CreateImageForm(
        byte[] bytes)
    {
        var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType =
            new MediaTypeHeaderValue("image/png");

        form.Add(file, "file", "test.png");

        return form;
    }
}

// Fake Cloudinary implementation for Integration Tests
internal sealed class FakeImageStorageRepository
    : IImageStorageRepository
{
    public List<string> UploadedIds { get; } = [];
    public List<string> DeletedIds { get; } = [];

    public bool FailDeletion { get; set; }

    public Task<(string ImageUrl, string PublicId)> UploadAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var id = $"test-images/{Guid.NewGuid():N}";

        UploadedIds.Add(id);

        return Task.FromResult((
            $"https://example.invalid/{id}.png",
            id
        ));
    }

    public Task DeleteAsync(string publicId)
    {
        if (FailDeletion)
            throw new InvalidOperationException(
                "FAKE_CLOUDINARY_DELETE_FAILED");

        DeletedIds.Add(publicId);
        return Task.CompletedTask;
    }
}
