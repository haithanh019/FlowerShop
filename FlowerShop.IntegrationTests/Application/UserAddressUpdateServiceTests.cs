
using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.UserAddresses;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class UserAddressUpdateServiceTests
{
    [Fact]
    public async Task Update_ShouldPreserveOwnershipDefaultAndTimestamps()
    {
        var connection = GetTestConnection();

        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var service = scope.ServiceProvider
            .GetRequiredService<IUserAddressService>();

        var hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<User>>();

        var owner = NewUser(hasher);
        var other = NewUser(hasher);
        var inactive = NewUser(hasher);
        inactive.IsActive = false;

        var admin = NewUser(hasher);
        admin.Role = UserRole.Admin;

        var userIds = new[]
        {
            owner.Id,
            other.Id,
            inactive.Id,
            admin.Id
        };

        var createdAt = DateTime.UtcNow.AddDays(-2);

        var defaultAddress = NewAddress(
            owner.Id, "Default", createdAt, true);

        var secondaryAddress = NewAddress(
            owner.Id, "Secondary", createdAt);

        var privateAddress = NewAddress(
            other.Id, "Private", createdAt, true);

        try
        {
            db.Users.AddRange(owner, other, inactive, admin);

            db.UserAddresses.AddRange(
                defaultAddress,
                secondaryAddress,
                privateAddress);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // 1. Capture persisted metadata before update.
            var before = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.Id == defaultAddress.Id);

            // 2. Update the default address.
            var request = ValidAddress();
            request.RecipientName = "  Updated Recipient  ";
            request.RecipientPhone = " 090 111 2222 ";
            request.AddressLine = " 456 Flower Road ";
            request.CountryCode = " vn ";
            request.PostalCode = "   ";

            var updated = await service.UpdateAsync(
                owner.Id,
                defaultAddress.Id,
                request);

            Assert.Equal(
                defaultAddress.Id,
                updated.AddressId);

            Assert.Equal(
                "Updated Recipient",
                updated.RecipientName);

            Assert.Equal(
                "090 111 2222",
                updated.RecipientPhone);

            Assert.Equal(
                "456 Flower Road",
                updated.AddressLine);

            Assert.Equal("VN", updated.CountryCode);
            Assert.Null(updated.PostalCode);

            Assert.True(updated.IsDefault);
            Assert.NotNull(updated.UpdatedAtUtc);

            // 3. Verify committed data.
            var stored = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.Id == defaultAddress.Id);

            Assert.Equal(owner.Id, stored.UserId);
            Assert.Equal(before.Id, stored.Id);
            Assert.Equal(before.CreatedAt, stored.CreatedAt);

            Assert.True(stored.IsDefault);
            Assert.NotNull(stored.UpdatedAt);

            Assert.Equal(
                "Updated Recipient",
                stored.RecipientName);

            Assert.Equal(
                "456 Flower Road",
                stored.AddressLine);

            Assert.Equal("VN", stored.CountryCode);
            Assert.Null(stored.PostalCode);

            // 4. Updating a non-default address must not
            // turn it into a default address.
            var secondaryRequest = ValidAddress();
            secondaryRequest.RecipientName =
                "Updated Secondary";

            var secondary = await service.UpdateAsync(
                owner.Id,
                secondaryAddress.Id,
                secondaryRequest);

            Assert.False(secondary.IsDefault);

            var ownerAddresses = await db.UserAddresses
                .AsNoTracking()
                .Where(x => x.UserId == owner.Id)
                .ToListAsync();

            Assert.Equal(2, ownerAddresses.Count);

            Assert.Single(
                ownerAddresses,
                x => x.IsDefault);

            // 5. Other Customer's address must be hidden.
            var forbidden = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.UpdateAsync(
                    owner.Id,
                    privateAddress.Id,
                    ValidAddress()));

            Assert.Equal(
                BusinessErrorType.NotFound,
                forbidden.ErrorType);

            Assert.Equal(
                "ADDRESS_NOT_FOUND",
                forbidden.Message);

            // 6. Nonexistent address.
            var missing = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.UpdateAsync(
                    owner.Id,
                    Guid.NewGuid(),
                    ValidAddress()));

            Assert.Equal(
                "ADDRESS_NOT_FOUND",
                missing.Message);

            // 7. Reject invalid request fields.
            var invalidRequest = ValidAddress();
            invalidRequest.RecipientPhone = "abc";

            await Assert.ThrowsAsync<ValidationException>(
                () => service.UpdateAsync(
                    owner.Id,
                    defaultAddress.Id,
                    invalidRequest));

            // 8. Reject invalid identifiers.
            await Assert.ThrowsAsync<ValidationException>(
                () => service.UpdateAsync(
                    Guid.Empty,
                    defaultAddress.Id,
                    ValidAddress()));

            await Assert.ThrowsAsync<ValidationException>(
                () => service.UpdateAsync(
                    owner.Id,
                    Guid.Empty,
                    ValidAddress()));

            // 9. Reject inactive and Admin accounts.
            var inactiveError = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.UpdateAsync(
                    inactive.Id,
                    defaultAddress.Id,
                    ValidAddress()));

            Assert.Equal(
                "USER_NOT_FOUND",
                inactiveError.Message);

            var adminError = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.UpdateAsync(
                    admin.Id,
                    defaultAddress.Id,
                    ValidAddress()));

            Assert.Equal(
                "USER_NOT_FOUND",
                adminError.Message);

            // 10. Verify other Customer's data is untouched.
            var otherStored = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.Id == privateAddress.Id);

            Assert.Equal(other.Id, otherStored.UserId);

            Assert.Equal(
                "Private Recipient",
                otherStored.RecipientName);

            Assert.True(otherStored.IsDefault);
        }
        finally
        {
            db.ChangeTracker.Clear();

            await db.UserAddresses
                .Where(x => userIds.Contains(x.UserId))
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x => userIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ConcurrentUpdate_ShouldPreserveCompleteAddressData()
    {
        var connection = GetTestConnection();

        using var factory = new TestApiFactory(connection);
        using var seedScope = factory.Services.CreateScope();

        var db = seedScope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var hasher = seedScope.ServiceProvider
            .GetRequiredService<IPasswordHasher<User>>();

        var owner = NewUser(hasher);

        var address = NewAddress(
            owner.Id,
            "Original",
            DateTime.UtcNow.AddDays(-1),
            true);

        try
        {
            db.Users.Add(owner);
            db.UserAddresses.Add(address);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Each operation must have its own DbContext.
            using var firstScope =
                factory.Services.CreateScope();

            using var secondScope =
                factory.Services.CreateScope();

            var firstService = firstScope.ServiceProvider
                .GetRequiredService<IUserAddressService>();

            var secondService = secondScope.ServiceProvider
                .GetRequiredService<IUserAddressService>();

            var firstRequest = ValidAddress();
            firstRequest.RecipientName = "Alpha Recipient";
            firstRequest.AddressLine = "Alpha Street";

            var secondRequest = ValidAddress();
            secondRequest.RecipientName = "Beta Recipient";
            secondRequest.AddressLine = "Beta Street";

            // Execute both updates concurrently.
            var results = await Task.WhenAll(
                firstService.UpdateAsync(
                    owner.Id,
                    address.Id,
                    firstRequest),

                secondService.UpdateAsync(
                    owner.Id,
                    address.Id,
                    secondRequest));

            Assert.Equal(2, results.Length);

            Assert.All(results, result =>
            {
                Assert.Equal(address.Id, result.AddressId);
                Assert.True(result.IsDefault);
                Assert.NotNull(result.UpdatedAtUtc);
            });

            // Read the final committed state.
            var stored = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.Id == address.Id);

            Assert.Equal(owner.Id, stored.UserId);
            Assert.True(stored.IsDefault);
            Assert.NotNull(stored.UpdatedAt);

            // Final state must contain a complete payload
            // from one request, not mixed field values.
            if (stored.RecipientName == "Alpha Recipient")
            {
                Assert.Equal(
                    "Alpha Street",
                    stored.AddressLine);
            }
            else
            {
                Assert.Equal(
                    "Beta Recipient",
                    stored.RecipientName);

                Assert.Equal(
                    "Beta Street",
                    stored.AddressLine);
            }
        }
        finally
        {
            db.ChangeTracker.Clear();

            await db.UserAddresses
                .Where(x => x.UserId == owner.Id)
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x => x.Id == owner.Id)
                .ExecuteDeleteAsync();
        }
    }

    private static User NewUser(
        IPasswordHasher<User> hasher)
    {
        var user = new User
        {
            FullName = "Address Update Customer",
            Email =
                $"address-update-{Guid.NewGuid():N}@example.invalid",
            Role = UserRole.Customer,
            IsActive = true
        };

        user.PasswordHash = hasher.HashPassword(
            user,
            "FlowerShop#2026");

        return user;
    }

    private static UserAddress NewAddress(
        Guid userId,
        string label,
        DateTime createdAt,
        bool isDefault = false)
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
            IsDefault = isDefault,
            CreatedAt = createdAt
        };
    }

    private static UserAddressUpsertDto ValidAddress()
    {
        return new UserAddressUpsertDto
        {
            RecipientName = "Nguyen Van A",
            RecipientPhone = "0901234567",
            AddressLine = "123 Nguyen Van Linh",
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
