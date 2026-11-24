using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.UserProfiles;
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

public sealed class UserProfileHttpTests
{
    private const string Route = "/api/me/profile";
    private const string Password = "FlowerShop#2026";

    [Fact]
    public async Task ProfileHttp_ShouldNormalizePreserveProtectedFieldsAndIsolateCustomers()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var client = CreateClient(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewUser(hasher, "Owner");
        owner.PhoneNumber = "0901234567";
        owner.IsEmailVerified = false;
        var other = NewUser(hasher, "Other");
        other.PhoneNumber = "0987654321";
        other.IsEmailVerified = true;
        Guid[] ids = [owner.Id, other.Id];

        try
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await ReadUserAsync(db, owner.Id);
            var otherBefore = await ReadUserAsync(db, other.Id);

            UseToken(client, await LoginAsync(client, owner));
            // A query parameter cannot override the JWT subject.
            var spoofedRoute = $"{Route}?userId={other.Id}";
            using (var get = await client.GetAsync(spoofedRoute))
            {
                var profile = await ReadProfileAsync(get);
                Assert.Equal(before.Id, profile.UserId);
                Assert.Equal(before.FullName, profile.FullName);
                Assert.Equal(before.Email, profile.Email);
                Assert.Equal(before.PhoneNumber, profile.PhoneNumber);
                Assert.False(profile.IsEmailVerified);
                Assert.Equal(before.CreatedAt, profile.CreatedAtUtc);
                Assert.Equal(DateTimeKind.Utc, profile.CreatedAtUtc.Kind);
                Assert.Null(profile.UpdatedAtUtc);
            }

            // Unknown JSON fields are ignored by the current API's JSON binding.
            // The identity and server-owned fields must not change.
            var malicious = new
            {
                fullName = "  Updated Owner  ",
                phoneNumber = " +84 901 234 567 ",
                userId = other.Id,
                id = other.Id,
                email = other.Email,
                role = "Admin",
                passwordHash = "not-a-password-hash",
                isActive = false,
                isEmailVerified = true,
                createdAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                updatedAt = new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc)
            };
            using var put = await client.PutAsJsonAsync(spoofedRoute, malicious);
            var updated = await ReadProfileAsync(put);
            Assert.Equal(owner.Id, updated.UserId);
            Assert.Equal("Updated Owner", updated.FullName);
            Assert.Equal("+84 901 234 567", updated.PhoneNumber);
            Assert.Equal(before.Email, updated.Email);
            Assert.False(updated.IsEmailVerified);
            Assert.NotNull(updated.UpdatedAtUtc);
            Assert.Equal<DateTimeKind?>(DateTimeKind.Utc, updated.UpdatedAtUtc?.Kind);
            var stored = await ReadUserAsync(db, owner.Id);
            AssertProtectedFields(before, stored);
            Assert.Equal(updated.FullName, stored.FullName);
            Assert.Equal(updated.PhoneNumber, stored.PhoneNumber);
            Assert.Equal(updated.UpdatedAtUtc, stored.UpdatedAt);

            using (var get = await client.GetAsync(Route))
                Assert.Equal(updated, await ReadProfileAsync(get));

            // Normalized repeat PUT must not advance UpdatedAt.
            using (var repeated = await client.PutAsJsonAsync(Route,
                new UpdateUserProfileDto
                {
                    FullName = " Updated Owner ",
                    PhoneNumber = " +84 901 234 567 "
                }))
                Assert.Equal(updated, await ReadProfileAsync(repeated));
            Assert.Equal(stored.UpdatedAt, (await ReadUserAsync(db, owner.Id)).UpdatedAt);

            using (var clear = await client.PutAsJsonAsync(Route,
                new UpdateUserProfileDto { FullName = "Updated Owner", PhoneNumber = "   " }))
                Assert.Null((await ReadProfileAsync(clear)).PhoneNumber);
            Assert.Null((await ReadUserAsync(db, owner.Id)).PhoneNumber);

            // Omitting PhoneNumber in PUT also clears the value.
            using (var restore = await client.PutAsJsonAsync(Route,
                new UpdateUserProfileDto { FullName = "Updated Owner", PhoneNumber = "0901234567" }))
                Assert.Equal("0901234567", (await ReadProfileAsync(restore)).PhoneNumber);
            using (var omit = await client.PutAsJsonAsync(Route, new { fullName = "Updated Owner" }))
                Assert.Null((await ReadProfileAsync(omit)).PhoneNumber);
            var final = await ReadUserAsync(db, owner.Id);
            Assert.Null(final.PhoneNumber);
            AssertProtectedFields(before, final);

            // The other Customer is unchanged and can read only their own profile.
            AssertUnchanged(otherBefore, await ReadUserAsync(db, other.Id));
            UseToken(client, await LoginAsync(client, other));
            using var otherGet = await client.GetAsync($"{Route}?userId={owner.Id}");
            var otherProfile = await ReadProfileAsync(otherGet);
            Assert.Equal(other.Id, otherProfile.UserId);
            Assert.Equal(otherBefore.FullName, otherProfile.FullName);
            Assert.Equal(otherBefore.Email, otherProfile.Email);
            Assert.Equal(otherBefore.PhoneNumber, otherProfile.PhoneNumber);
            Assert.True(otherProfile.IsEmailVerified);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Users.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ProfileHttp_ShouldEnforceJwtRoleAndValidationWithoutMutation()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var client = CreateClient(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var customer = NewUser(hasher, "Customer");
        var admin = NewUser(hasher, "Admin", UserRole.Admin);
        Guid[] ids = [customer.Id, admin.Id];
        var valid = new UpdateUserProfileDto { FullName = "Changed Customer", PhoneNumber = "0901234567" };

        try
        {
            db.Users.AddRange(customer, admin);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await ReadUserAsync(db, customer.Id);
            var adminBefore = await ReadUserAsync(db, admin.Id);

            await AssertBothStatusAsync(client, valid, HttpStatusCode.Unauthorized);
            UseToken(client, "invalid.jwt.token");
            await AssertBothStatusAsync(client, valid, HttpStatusCode.Unauthorized);

            UseToken(client, await LoginAsync(client, admin));
            await AssertBothStatusAsync(client, valid, HttpStatusCode.Forbidden);

            var customerToken = await LoginAsync(client, customer);
            UseToken(client, customerToken);
            using (var invalid = await client.PutAsJsonAsync(Route,
                new UpdateUserProfileDto { FullName = "A", PhoneNumber = "abc" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                var problem = Assert.IsType<ValidationProblemDetails>(
                    await invalid.Content.ReadFromJsonAsync<ValidationProblemDetails>());
                Assert.Contains(problem.Errors, entry =>
                    string.Equals(entry.Key, "FullName", StringComparison.OrdinalIgnoreCase) &&
                    entry.Value.Contains("FULL_NAME_TOO_SHORT"));
                Assert.Contains(problem.Errors, entry =>
                    string.Equals(entry.Key, "PhoneNumber", StringComparison.OrdinalIgnoreCase) &&
                    entry.Value.Contains("PHONE_NUMBER_INVALID"));
            }
            using (var missingName = await client.PutAsJsonAsync(Route, new { phoneNumber = "0901234567" }))
                Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);
            using (var malformed = new StringContent("{", Encoding.UTF8, "application/json"))
            using (var response = await client.PutAsync(Route, malformed))
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using (var empty = new StringContent("null", Encoding.UTF8, "application/json"))
            using (var response = await client.PutAsync(Route, empty))
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            AssertUnchanged(before, await ReadUserAsync(db, customer.Id));
            AssertUnchanged(adminBefore, await ReadUserAsync(db, admin.Id));

            // Old JWT must be rejected after account state changes.
            await db.Users.Where(x => x.Id == customer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            await AssertBothStatusAsync(client, valid, HttpStatusCode.Unauthorized);
            await db.Users.Where(x => x.Id == customer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true)
                    .SetProperty(x => x.Role, UserRole.Admin));
            await AssertBothStatusAsync(client, valid, HttpStatusCode.Unauthorized);
            await db.Users.Where(x => x.Id == customer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Role, UserRole.Customer));
            AssertUnchanged(before, await ReadUserAsync(db, customer.Id));
            AssertUnchanged(adminBefore, await ReadUserAsync(db, admin.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Users.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ConcurrentHttpProfileUpdate_ShouldPersistOneCompletePayload()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var firstClient = CreateClient(factory);
        using var secondClient = CreateClient(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var customer = NewUser(hasher, "Concurrent");
        customer.PhoneNumber = "0901234567";

        try
        {
            db.Users.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await ReadUserAsync(db, customer.Id);
            var token = await LoginAsync(firstClient, customer);
            UseToken(firstClient, token);
            UseToken(secondClient, token);

            // Each request retains one original value. If updates race and EF
            // detects changes against a stale snapshot, a mixed payload is possible.
            var first = new UpdateUserProfileDto
            {
                FullName = "Alpha Customer",
                PhoneNumber = before.PhoneNumber
            };
            var second = new UpdateUserProfileDto
            {
                FullName = before.FullName,
                PhoneNumber = "0987654321"
            };

            var responses = await Task.WhenAll(
                firstClient.PutAsJsonAsync(Route, first),
                secondClient.PutAsJsonAsync(Route, second));
            using var firstResponse = responses[0];
            using var secondResponse = responses[1];
            var firstResult = await ReadProfileAsync(firstResponse);
            var secondResult = await ReadProfileAsync(secondResponse);
            Assert.Equal(customer.Id, firstResult.UserId);
            Assert.Equal(customer.Id, secondResult.UserId);
            Assert.Equal(first.FullName, firstResult.FullName);
            Assert.Equal(first.PhoneNumber, firstResult.PhoneNumber);
            Assert.Equal(second.FullName, secondResult.FullName);
            Assert.Equal(second.PhoneNumber, secondResult.PhoneNumber);

            var stored = await ReadUserAsync(db, customer.Id);
            Assert.True(
                (stored.FullName == first.FullName && stored.PhoneNumber == first.PhoneNumber) ||
                (stored.FullName == second.FullName && stored.PhoneNumber == second.PhoneNumber),
                "Final persisted profile must match one complete PUT payload.");
            AssertProtectedFields(before, stored);
            Assert.NotNull(stored.UpdatedAt);
            using var get = await firstClient.GetAsync(Route);
            var final = await ReadProfileAsync(get);
            Assert.Equal(stored.FullName, final.FullName);
            Assert.Equal(stored.PhoneNumber, final.PhoneNumber);
            Assert.Equal(stored.UpdatedAt, final.UpdatedAtUtc);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Users.Where(x => x.Id == customer.Id).ExecuteDeleteAsync();
        }
    }

    private static async Task<UserProfileDto> ReadProfileAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        // Assert the actual JSON contract, not just the deserialized DTO.
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        string[] expected = ["userId", "fullName", "email", "phoneNumber",
            "isEmailVerified", "createdAtUtc", "updatedAtUtc"];
        var actual = document.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        return Assert.IsType<UserProfileDto>(
            await response.Content.ReadFromJsonAsync<UserProfileDto>());
    }

    private static async Task AssertBothStatusAsync(HttpClient client,
        UpdateUserProfileDto dto, HttpStatusCode expected)
    {
        using var get = await client.GetAsync(Route);
        Assert.Equal(expected, get.StatusCode);
        using var put = await client.PutAsJsonAsync(Route, dto);
        Assert.Equal(expected, put.StatusCode);
    }

    private static Task<User> ReadUserAsync(FlowerShopDbContext db, Guid id) =>
        db.Users.AsNoTracking().SingleAsync(x => x.Id == id);

    private static void AssertProtectedFields(User before, User after)
    {
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.Role, after.Role);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.IsEmailVerified, after.IsEmailVerified);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    private static void AssertUnchanged(User before, User after)
    {
        AssertProtectedFields(before, after);
        Assert.Equal(before.FullName, after.FullName);
        Assert.Equal(before.PhoneNumber, after.PhoneNumber);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    private static HttpClient CreateClient(TestApiFactory factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

    private static async Task<string> LoginAsync(HttpClient client, User user)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginDto { Email = user.Email, Password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = Assert.IsType<LoginResponseDto>(
            await response.Content.ReadFromJsonAsync<LoginResponseDto>());
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        return login.AccessToken;
    }

    private static void UseToken(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static User NewUser(IPasswordHasher<User> hasher, string label,
        UserRole role = UserRole.Customer)
    {
        var user = new User
        {
            FullName = $"{label} Profile Customer",
            Email = $"profile-http-{Guid.NewGuid():N}@example.invalid",
            Role = role,
            IsActive = true,
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)
        };
        user.PasswordHash = hasher.HashPassword(user, Password);
        return user;
    }

    private static string GetTestConnection()
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test", StringComparison.OrdinalIgnoreCase) ||
            sql.MultipleActiveResultSets)
            throw new InvalidOperationException("Use FlowerShop_Test with MARS disabled.");
        return connection;
    }
}
