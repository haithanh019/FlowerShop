using System.Net;
using System.Net.Http.Json;
using FlowerShop.Application.DTOs.Auth;
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

public sealed class RegisterHttpTests
{
    private const string RegisterUrl = "/api/auth/register";
    private const string LoginUrl = "/api/auth/login";
    private const string Password = "FlowerShop#2026";

    [Fact]
    public async Task Register_ShouldValidateCreateCustomerAndAllowLogin()
    {
        var connection = GetTestConnection();

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

        var email =
            $"register-http-{Guid.NewGuid():N}@example.invalid";

        try
        {
            // 1. Anonymous registration.
            // Attempt to inject server-owned properties.
            var request = new
            {
                fullName = "  Nguyen Van A  ",
                email = $"  {email.ToUpperInvariant()}  ",
                password = Password,
                confirmPassword = Password,

                role = 1,
                isActive = false,
                isEmailVerified = true,
                passwordHash = "FORGED_PASSWORD_HASH"
            };

            using var response = await client.PostAsJsonAsync(
                RegisterUrl, request);

            Assert.Equal(
                HttpStatusCode.Created,
                response.StatusCode);

            var result = await response.Content
                .ReadFromJsonAsync<RegisterResponseDto>();

            var registered = Assert.IsType<
                RegisterResponseDto>(result);

            Assert.NotEqual(Guid.Empty, registered.UserId);
            Assert.Equal("Nguyen Van A", registered.FullName);
            Assert.Equal(email, registered.Email);

            // 2. Response must not expose sensitive fields.
            var responseJson =
                await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(
                "password",
                responseJson,
                StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(
                "accessToken",
                responseJson,
                StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(
                "refreshToken",
                responseJson,
                StringComparison.OrdinalIgnoreCase);

            // 3. Verify persisted User.
            var user = await db.Users.AsNoTracking()
                .SingleAsync(x => x.Id == registered.UserId);

            Assert.Equal(UserRole.Customer, user.Role);
            Assert.True(user.IsActive);
            Assert.False(user.IsEmailVerified);

            Assert.Equal(email, user.Email);
            Assert.Equal("Nguyen Van A", user.FullName);

            // Client-supplied values must not override
            // server-owned account properties.
            Assert.NotEqual(
                "FORGED_PASSWORD_HASH",
                user.PasswordHash);

            Assert.NotEqual(
                Password,
                user.PasswordHash);

            var hashResult = hasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                Password);

            Assert.NotEqual(
                PasswordVerificationResult.Failed,
                hashResult);

            // 4. Duplicate email should return 409.
            var duplicateRequest = new RegisterDto
            {
                FullName = "Another Customer",
                Email = email,
                Password = Password,
                ConfirmPassword = Password
            };

            using var duplicate = await client.PostAsJsonAsync(
                RegisterUrl, duplicateRequest);

            Assert.Equal(
                HttpStatusCode.Conflict,
                duplicate.StatusCode);

            // 5. Invalid email should return 400.
            using var invalidEmail = await client.PostAsJsonAsync(
                RegisterUrl,
                new RegisterDto
                {
                    FullName = "Invalid Customer",
                    Email = "not-an-email",
                    Password = Password,
                    ConfirmPassword = Password
                });

            Assert.Equal(
                HttpStatusCode.BadRequest,
                invalidEmail.StatusCode);

            // 6. Password confirmation mismatch => 400.
            using var mismatch = await client.PostAsJsonAsync(
                RegisterUrl,
                new RegisterDto
                {
                    FullName = "Invalid Customer",
                    Email = $"invalid-{Guid.NewGuid():N}@example.invalid",
                    Password = Password,
                    ConfirmPassword = "DifferentPassword#2026"
                });

            Assert.Equal(
                HttpStatusCode.BadRequest,
                mismatch.StatusCode);

            // 7. Exactly one User should exist.
            Assert.Equal(
                1,
                await db.Users.AsNoTracking()
                    .CountAsync(x => x.Email == email));

            // 8. Registered Customer can log in.
            using var loginResponse = await client.PostAsJsonAsync(
                LoginUrl,
                new LoginDto
                {
                    Email = $"  {email.ToUpperInvariant()}  ",
                    Password = Password
                });

            Assert.Equal(
                HttpStatusCode.OK,
                loginResponse.StatusCode);

            var login = await loginResponse.Content
                .ReadFromJsonAsync<LoginResponseDto>();

            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsType<LoginResponseDto>(login)
                    .AccessToken));
        }
        finally
        {
            db.ChangeTracker.Clear();

            await db.Users
                .Where(x => x.Email == email)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ConcurrentRegistration_ShouldCreateOnlyOneCustomer()
    {
        var connection = GetTestConnection();

        using var factory = new TestApiFactory(connection);

        using var firstClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        using var secondClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var email =
            $"register-race-{Guid.NewGuid():N}@example.invalid";

        var firstRequest = new RegisterDto
        {
            FullName = "First Customer",
            Email = email,
            Password = Password,
            ConfirmPassword = Password
        };

        var secondRequest = new RegisterDto
        {
            FullName = "Second Customer",
            Email = $"  {email.ToUpperInvariant()}  ",
            Password = Password,
            ConfirmPassword = Password
        };

        try
        {
            // Two HTTP requests enter the application
            // concurrently using separate DI scopes.
            var firstTask = firstClient.PostAsJsonAsync(
                RegisterUrl, firstRequest);

            var secondTask = secondClient.PostAsJsonAsync(
                RegisterUrl, secondRequest);

            var responses = await Task.WhenAll(
                firstTask, secondTask);

            try
            {
                // One request succeeds, the other conflicts.
                Assert.Equal(
                    1,
                    responses.Count(x =>
                        x.StatusCode == HttpStatusCode.Created));

                Assert.Equal(
                    1,
                    responses.Count(x =>
                        x.StatusCode == HttpStatusCode.Conflict));

                // Database invariant: only one Customer.
                var users = await db.Users
                    .AsNoTracking()
                    .Where(x => x.Email == email)
                    .ToListAsync();

                var user = Assert.Single(users);

                Assert.Equal(UserRole.Customer, user.Role);
                Assert.True(user.IsActive);
                Assert.False(user.IsEmailVerified);
            }
            finally
            {
                foreach (var response in responses)
                    response.Dispose();
            }
        }
        finally
        {
            db.ChangeTracker.Clear();

            await db.Users
                .Where(x => x.Email == email)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Register_ShouldReturn429_WithoutLimitingLogin()
    {
        var connection = GetTestConnection();

        using var factory = new TestApiFactory(connection);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        // Invalid requests do not create Users,
        // but still consume Register rate-limit permits.
        var invalidRequest = new RegisterDto
        {
            FullName = "Rate Limit Test",
            Email = "invalid-email",
            Password = Password,
            ConfirmPassword = Password
        };

        // Current policy permits five registration requests.
        for (var i = 0; i < 5; i++)
        {
            using var response = await client.PostAsJsonAsync(
                RegisterUrl, invalidRequest);

            Assert.Equal(
                HttpStatusCode.BadRequest,
                response.StatusCode);
        }

        // Sixth request exceeds the Register limit.
        using var rejected = await client.PostAsJsonAsync(
            RegisterUrl, invalidRequest);

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            rejected.StatusCode);

        // Login has an independent rate-limiting policy.
        using var login = await client.PostAsJsonAsync(
            LoginUrl,
            new LoginDto
            {
                Email = "unknown@example.invalid",
                Password = Password
            });

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            login.StatusCode);
    }

    private static string GetTestConnection()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                "Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        }

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

        return connection;
    }
}
