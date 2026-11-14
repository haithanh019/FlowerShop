using FlowerShop.API;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ApiProgram = FlowerShop.API.Program;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public class AuthHttpTests
{
    [Fact]
    public async Task LoginAndAdminAuthorization_ShouldWork()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing test connection.");

        if (new SqlConnectionStringBuilder(connection).InitialCatalog
            != "FlowerShop_Test")
            throw new InvalidOperationException("Invalid test database.");

        using var factory = new TestApiFactory(connection);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var actualDb = new SqlConnectionStringBuilder(
            db.Database.GetConnectionString());

        if (actualDb.InitialCatalog != "FlowerShop_Test")
            throw new InvalidOperationException("API is not using the test DB.");

        const string password = "TestPassword#2026";

        User NewUser(UserRole role)
        {
            var user = new User
            {
                FullName = "Integration Test",
                Email = $"test-{Guid.NewGuid():N}@example.invalid",
                Role = role,
                IsActive = true
            };

            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var admin = NewUser(UserRole.Admin);
        var customer = NewUser(UserRole.Customer);

        db.Users.AddRange(admin, customer);
        await db.SaveChangesAsync();

        try
        {
            var invalid = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginDto { Email = "invalid", Password = password });

            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            var wrong = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginDto { Email = admin.Email, Password = "wrong" });

            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

            async Task<LoginResponseDto> Login(User user)
            {
                var response = await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new LoginDto { Email = user.Email, Password = password });

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                return (await response.Content
                    .ReadFromJsonAsync<LoginResponseDto>())!;
            }

            var customerToken = await Login(customer);
            var adminToken = await Login(admin);

            Assert.False(string.IsNullOrWhiteSpace(adminToken.AccessToken));

            var url = $"/api/categories/{Guid.NewGuid()}";
            var dto = new UpdateCategoryDto
            {
                Name = "Test",
                Slug = "test-category"
            };

            // Anonymous -> 401
            var anonymous = await client.PutAsJsonAsync(url, dto);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            // Customer -> 403
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", customerToken.AccessToken);

            var forbidden = await client.PutAsJsonAsync(url, dto);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            // Admin -> CategoryService -> 404
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", adminToken.AccessToken);

            var notFound = await client.PutAsJsonAsync(url, dto);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }
        finally
        {
            db.Users.RemoveRange(admin, customer);
            await db.SaveChangesAsync();
        }
    }

    private sealed class TestApiFactory(string connection)
        : WebApplicationFactory<ApiProgram>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = connection,
                        ["Jwt:Issuer"] = "FlowerShop.Tests",
                        ["Jwt:Audience"] = "FlowerShop.Tests.Client",
                        ["Jwt:Key"] = Convert.ToBase64String(
                            RandomNumberGenerator.GetBytes(48))
                    }));

            return base.CreateHost(builder);
        }
    }
}
