using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.DTOs.UserAddresses;
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

public sealed class UserAddressHttpTests
{
    private const string BaseRoute = "/api/me/addresses";
    private const string Password = "FlowerShop#2026";

    [Fact]
    public async Task AddressHttp_ShouldSupportCrudDefaultAndPreserveOrderSnapshot()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var client = CreateClient(factory);
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var customer = NewUser(hasher, "Customer", UserRole.Customer);
        Guid? snapshotOrderId = null;

        try
        {
            db.Users.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            UseToken(client, await LoginAsync(client, customer));

            // First address becomes default. POST must return a Location header.
            var firstRequest = ValidAddress();
            firstRequest.RecipientName = "  First Recipient  ";
            firstRequest.CountryCode = " vn ";

            using var firstResponse = await client.PostAsJsonAsync(BaseRoute, firstRequest);
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
            var first = await ReadAddressAsync(firstResponse);
            Assert.True(first.IsDefault);
            Assert.Equal("First Recipient", first.RecipientName);
            Assert.Equal("VN", first.CountryCode);
            Assert.NotEqual(Guid.Empty, first.AddressId);
            Assert.True(firstResponse.Headers.Location?.ToString().EndsWith(
                    $"{BaseRoute}/{first.AddressId}",
                    StringComparison.OrdinalIgnoreCase));

            // Existing Order shipping fields are snapshots, not linked to UserAddress.
            var order = new Order
            {
                OrderCode = $"ADDR-{Guid.NewGuid():N}",
                UserId = customer.Id,
                RecipientName = first.RecipientName,
                RecipientPhone = first.RecipientPhone,
                ShippingAddressLine = first.AddressLine,
                ShippingWard = first.Ward,
                ShippingDistrict = first.District,
                ShippingProvince = first.Province,
                ShippingCountryCode = first.CountryCode,
                ShippingPostalCode = first.PostalCode,
                Subtotal = 0m,
                ShippingFee = 0m,
                DiscountAmount = 0m,
                TotalAmount = 0m
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            snapshotOrderId = order.Id;
            db.ChangeTracker.Clear();

            // Second and third addresses do not replace the existing default.
            var secondRequest = ValidAddress();
            secondRequest.RecipientName = "Second Recipient";
            using var secondResponse = await client.PostAsJsonAsync(BaseRoute, secondRequest);
            Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
            var second = await ReadAddressAsync(secondResponse);
            Assert.False(second.IsDefault);

            var thirdRequest = ValidAddress();
            thirdRequest.RecipientName = "Third Recipient";
            using var thirdResponse = await client.PostAsJsonAsync(BaseRoute, thirdRequest);
            Assert.Equal(HttpStatusCode.Created, thirdResponse.StatusCode);
            var third = await ReadAddressAsync(thirdResponse);
            Assert.False(third.IsDefault);

            // GET collection and GET detail.
            using (var listResponse = await client.GetAsync(BaseRoute))
            {
                Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
                var list = await listResponse.Content
                    .ReadFromJsonAsync<List<UserAddressDto>>();
                var addresses = Assert.IsType<List<UserAddressDto>>(list);
                Assert.Equal(3, addresses.Count);
                Assert.Equal(first.AddressId, addresses[0].AddressId);
                Assert.Single(addresses, x => x.IsDefault);
            }

            using (var detailResponse = await client.GetAsync(
                $"{BaseRoute}/{second.AddressId}"))
            {
                Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
                Assert.Equal(second.AddressId,
                    (await ReadAddressAsync(detailResponse)).AddressId);
            }

            // PUT updates editable fields, preserving ownership/default.
            var updateRequest = ValidAddress();
            updateRequest.RecipientName = "  Changed Recipient  ";
            updateRequest.AddressLine = " 456 New Street ";
            updateRequest.PostalCode = "   ";
            using (var putResponse = await client.PutAsJsonAsync(
                $"{BaseRoute}/{first.AddressId}", updateRequest))
            {
                Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
                var updated = await ReadAddressAsync(putResponse);
                Assert.Equal(first.AddressId, updated.AddressId);
                Assert.Equal("Changed Recipient", updated.RecipientName);
                Assert.Equal("456 New Street", updated.AddressLine);
                Assert.Null(updated.PostalCode);
                Assert.True(updated.IsDefault);
                Assert.NotNull(updated.UpdatedAtUtc);
            }

            // PATCH selects a different default.
            using (var patchResponse = await PatchDefaultAsync(client, second.AddressId))
            {
                Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
                var chosen = await ReadAddressAsync(patchResponse);
                Assert.Equal(second.AddressId, chosen.AddressId);
                Assert.True(chosen.IsDefault);
            }

            var timestampBeforeRepeat = await db.UserAddresses.AsNoTracking()
                .Where(x => x.Id == second.AddressId)
                .Select(x => x.UpdatedAt)
                .SingleAsync();

            // Idempotency: selecting the current default does not modify UpdatedAt.
            using (var repeated = await PatchDefaultAsync(client, second.AddressId))
            {
                Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
                Assert.True((await ReadAddressAsync(repeated)).IsDefault);
            }

            var timestampAfterRepeat = await db.UserAddresses.AsNoTracking()
                .Where(x => x.Id == second.AddressId)
                .Select(x => x.UpdatedAt)
                .SingleAsync();
            Assert.Equal(timestampBeforeRepeat, timestampAfterRepeat);

            // Establish the exact replacement order from persisted SQL timestamps.
            var expectedReplacement = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == customer.Id && x.Id != second.AddressId)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .FirstAsync();

            // DELETE default -> promote oldest remaining address.
            using (var deleteResponse = await client.DeleteAsync(
                $"{BaseRoute}/{second.AddressId}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            var remaining = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == customer.Id).ToListAsync();
            Assert.Equal(2, remaining.Count);
            Assert.Equal(expectedReplacement,
                Assert.Single(remaining, x => x.IsDefault).Id);

            // DELETE all remaining addresses using their actual persisted IDs.
            foreach (var remainingAddress in remaining)
            {
                using var delete = await client.DeleteAsync(
                    $"{BaseRoute}/{remainingAddress.Id}");
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }

            using (var emptyResponse = await client.GetAsync(BaseRoute))
            {
                Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
                var empty = await emptyResponse.Content
                    .ReadFromJsonAsync<List<UserAddressDto>>();
                Assert.Empty(Assert.IsType<List<UserAddressDto>>(empty));
            }

            using (var deletedDetail = await client.GetAsync(
                $"{BaseRoute}/{first.AddressId}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, deletedDetail.StatusCode);
            }

            // Address edits/deletions must NOT mutate previously persisted Order snapshots.
            var persistedOrder = await db.Orders.AsNoTracking()
                .SingleAsync(x => x.Id == order.Id);
            Assert.Equal(first.RecipientName, persistedOrder.RecipientName);
            Assert.Equal(first.RecipientPhone, persistedOrder.RecipientPhone);
            Assert.Equal(first.AddressLine, persistedOrder.ShippingAddressLine);
            Assert.Equal(first.Ward, persistedOrder.ShippingWard);
            Assert.Equal(first.District, persistedOrder.ShippingDistrict);
            Assert.Equal(first.Province, persistedOrder.ShippingProvince);
            Assert.Equal(first.CountryCode, persistedOrder.ShippingCountryCode);
            Assert.Equal(first.PostalCode, persistedOrder.ShippingPostalCode);
        }
        finally
        {
            db.ChangeTracker.Clear();
            if (snapshotOrderId is Guid orderId)
            {
                await db.Orders.Where(x => x.Id == orderId)
                    .ExecuteDeleteAsync();
            }
            await db.UserAddresses.Where(x => x.UserId == customer.Id)
                .ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == customer.Id)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task AddressHttp_ShouldEnforceAuthorizationOwnershipAndValidation()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var client = CreateClient(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var owner = NewUser(hasher, "Owner", UserRole.Customer);
        var other = NewUser(hasher, "Other", UserRole.Customer);
        var admin = NewUser(hasher, "Admin", UserRole.Admin);
        var privateAddress = NewAddress(other.Id, "Private", isDefault: true);
        var userIds = new[] { owner.Id, other.Id, admin.Id };

        try
        {
            db.Users.AddRange(owner, other, admin);
            db.UserAddresses.Add(privateAddress);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Anonymous must not access protected endpoints.
            using (var getAnonymous = await client.GetAsync(BaseRoute))
                Assert.Equal(HttpStatusCode.Unauthorized, getAnonymous.StatusCode);
            using (var postAnonymous = await client.PostAsJsonAsync(BaseRoute, ValidAddress()))
                Assert.Equal(HttpStatusCode.Unauthorized, postAnonymous.StatusCode);

            // Admin JWT is authenticated, but role forbidden.
            UseToken(client, await LoginAsync(client, admin));
            using (var getAdmin = await client.GetAsync(BaseRoute))
                Assert.Equal(HttpStatusCode.Forbidden, getAdmin.StatusCode);
            using (var postAdmin = await client.PostAsJsonAsync(BaseRoute, ValidAddress()))
                Assert.Equal(HttpStatusCode.Forbidden, postAdmin.StatusCode);
            using (var patchAdmin = await PatchDefaultAsync(client, privateAddress.Id))
                Assert.Equal(HttpStatusCode.Forbidden, patchAdmin.StatusCode);

            UseToken(client, await LoginAsync(client, owner));

            // IDOR protection: other Customer's existing address is indistinguishable
            // from a nonexistent address.
            using (var getOther = await client.GetAsync(
                $"{BaseRoute}/{privateAddress.Id}"))
                Assert.Equal(HttpStatusCode.NotFound, getOther.StatusCode);
            using (var putOther = await client.PutAsJsonAsync(
                $"{BaseRoute}/{privateAddress.Id}", ValidAddress()))
                Assert.Equal(HttpStatusCode.NotFound, putOther.StatusCode);
            using (var deleteOther = await client.DeleteAsync(
                $"{BaseRoute}/{privateAddress.Id}"))
                Assert.Equal(HttpStatusCode.NotFound, deleteOther.StatusCode);
            using (var patchOther = await PatchDefaultAsync(client, privateAddress.Id))
                Assert.Equal(HttpStatusCode.NotFound, patchOther.StatusCode);

            using (var missing = await client.GetAsync(
                $"{BaseRoute}/{Guid.NewGuid()}"))
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            using (var emptyId = await client.GetAsync(
                $"{BaseRoute}/{Guid.Empty}"))
                Assert.Equal(HttpStatusCode.BadRequest, emptyId.StatusCode);

            // Invalid input should be rejected by API validation.
            var invalidPhone = ValidAddress();
            invalidPhone.RecipientPhone = "abc";
            using (var invalid = await client.PostAsJsonAsync(BaseRoute, invalidPhone))
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            var invalidCountry = ValidAddress();
            invalidCountry.CountryCode = "VNM";
            using (var invalid = await client.PostAsJsonAsync(BaseRoute, invalidCountry))
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            var missingName = ValidAddress();
            missingName.RecipientName = "   ";
            using (var invalid = await client.PostAsJsonAsync(BaseRoute, missingName))
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            // Rejected requests must not write rows or mutate the private address.
            Assert.False(await db.UserAddresses.AsNoTracking()
                .AnyAsync(x => x.UserId == owner.Id));
            var storedPrivate = await db.UserAddresses.AsNoTracking()
                .SingleAsync(x => x.Id == privateAddress.Id);
            Assert.Equal("Private Recipient", storedPrivate.RecipientName);
            Assert.True(storedPrivate.IsDefault);

            // Revoked account must fail validation even with an existing JWT.
            await db.Users.Where(x => x.Id == owner.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            using var disabled = await client.GetAsync(BaseRoute);
            Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.UserAddresses.Where(x => userIds.Contains(x.UserId))
                .ExecuteDeleteAsync();
            await db.Users.Where(x => userIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ConcurrentHttpSetDefault_ShouldKeepExactlyOneDefault()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var seedClient = CreateClient(factory);
        using var seedScope = factory.Services.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = seedScope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewUser(hasher, "Concurrency", UserRole.Customer);
        var previous = NewAddress(owner.Id, "Previous", isDefault: true);
        var candidate = NewAddress(owner.Id, "Candidate");

        try
        {
            db.Users.Add(owner);
            db.UserAddresses.AddRange(previous, candidate);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var token = await LoginAsync(seedClient, owner);
            using var firstClient = CreateClient(factory);
            using var secondClient = CreateClient(factory);
            UseToken(firstClient, token);
            UseToken(secondClient, token);

            // Separate HTTP requests get their own DI scopes and DbContexts.
            var firstTask = PatchDefaultAsync(firstClient, previous.Id);
            var secondTask = PatchDefaultAsync(secondClient, candidate.Id);
            var results = await Task.WhenAll(firstTask, secondTask);
            using var firstResult = results[0];
            using var secondResult = results[1];
            Assert.Equal(HttpStatusCode.OK, firstResult.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondResult.StatusCode);

            var stored = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == owner.Id).ToListAsync();
            Assert.Equal(2, stored.Count);
            Assert.Single(stored, x => x.IsDefault);
            Assert.All(stored, x => Assert.Equal(owner.Id, x.UserId));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.UserAddresses.Where(x => x.UserId == owner.Id)
                .ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == owner.Id)
                .ExecuteDeleteAsync();
        }
    }

    private static async Task<UserAddressDto> ReadAddressAsync(
        HttpResponseMessage response)
    {
        var result = await response.Content.ReadFromJsonAsync<UserAddressDto>();
        return Assert.IsType<UserAddressDto>(result);
    }

    private static async Task<HttpResponseMessage> PatchDefaultAsync(
        HttpClient client,
        Guid addressId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"{BaseRoute}/{addressId}/default");
        return await client.SendAsync(request);
    }

    private static async Task<string> LoginAsync(HttpClient client, User user)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginDto { Email = user.Email, Password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return Assert.IsType<LoginResponseDto>(login).AccessToken;
    }

    private static void UseToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    private static HttpClient CreateClient(TestApiFactory factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    private static User NewUser(
        IPasswordHasher<User> hasher, string label, UserRole role)
    {
        var user = new User
        {
            FullName = $"Address HTTP {label}",
            Email = $"address-http-{Guid.NewGuid():N}@example.invalid",
            Role = role,
            IsActive = true
        };
        user.PasswordHash = hasher.HashPassword(user, Password);
        return user;
    }

    private static UserAddress NewAddress(
        Guid userId, string label, bool isDefault = false)
    {
        return new UserAddress
        {
            UserId = userId,
            RecipientName = $"{label} Recipient",
            RecipientPhone = "0901234567",
            AddressLine = $"{label} Flower Street",
            Ward = "Hai Chau",
            District = "Hai Chau",
            Province = "Da Nang",
            CountryCode = "VN",
            PostalCode = "550000",
            IsDefault = isDefault
        };
    }

    private static UserAddressUpsertDto ValidAddress()
    {
        return new UserAddressUpsertDto
        {
            RecipientName = "Nguyen Van A",
            RecipientPhone = "0901234567",
            AddressLine = "123 Flower Street",
            Ward = "Hai Chau",
            District = "Hai Chau",
            Province = "Da Nang",
            CountryCode = "VN",
            PostalCode = "550000"
        };
    }

    private static string GetTestConnection()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase) || sql.MultipleActiveResultSets)
        {
            throw new InvalidOperationException(
                "Use FlowerShop_Test with MARS disabled.");
        }
        return connection;
    }
}
