using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
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

public sealed class UserAddressQueryServiceTests
{
    [Fact]
    public async Task AddressQueries_ShouldEnforceOwnershipAndOrdering()
    {
        // 1. Only use the dedicated test database.
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
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var service = scope.ServiceProvider
            .GetRequiredService<IUserAddressService>();

        var hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<User>>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        // 2. Roll back all seeded data.
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        try
        {
            var owner = NewUser("Owner");
            var other = NewUser("Other");

            owner.PasswordHash = hasher.HashPassword(
                owner, "FlowerShop#2026");

            other.PasswordHash = hasher.HashPassword(
                other, "FlowerShop#2026");

            var now = DateTime.UtcNow;

            var olderAddress = NewAddress(
                owner.Id,
                "Older",
                now.AddDays(-3));

            var newerAddress = NewAddress(
                owner.Id,
                "Newer",
                now.AddDays(-2));

            var defaultAddress = NewAddress(
                owner.Id,
                "Default",
                now.AddDays(-1),
                isDefault: true);

            var otherAddress = NewAddress(
                other.Id,
                "Private",
                now,
                isDefault: true);

            db.Users.AddRange(owner, other);

            db.UserAddresses.AddRange(
                olderAddress,
                newerAddress,
                defaultAddress,
                otherAddress);

            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();

            // 3. Get only the owner's addresses.
            var addresses = await service.GetAllAsync(
                owner.Id);

            Assert.Equal(3, addresses.Count);

            Assert.Equal(
                [
                    defaultAddress.Id,
                    olderAddress.Id,
                    newerAddress.Id
                ],
                [.. addresses.Select(x => x.AddressId)]);

            Assert.True(addresses[0].IsDefault);

            Assert.DoesNotContain(
                addresses,
                x => x.AddressId == otherAddress.Id);

            // 4. Get an owned address.
            var ownAddress = await service.GetByIdAsync(
                owner.Id,
                olderAddress.Id);

            Assert.Equal(
                olderAddress.Id,
                ownAddress.AddressId);

            Assert.Equal(
                "Older Recipient",
                ownAddress.RecipientName);

            Assert.Equal(
                DateTimeKind.Utc,
                ownAddress.CreatedAtUtc.Kind);

            // 5. Another user's address must be hidden.
            var forbidden = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.GetByIdAsync(
                    owner.Id,
                    otherAddress.Id));

            Assert.Equal(
                BusinessErrorType.NotFound,
                forbidden.ErrorType);

            Assert.Equal(
                "ADDRESS_NOT_FOUND",
                forbidden.Message);

            // 6. A nonexistent address must also return 404.
            var missing = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.GetByIdAsync(
                    owner.Id,
                    Guid.NewGuid()));

            Assert.Equal(
                BusinessErrorType.NotFound,
                missing.ErrorType);

            // 7. An owner with no addresses gets an empty list.
            var empty = await service.GetAllAsync(
                Guid.NewGuid());

            Assert.Empty(empty);

            // 8. Invalid input must be rejected.
            var invalidUser = await Assert.ThrowsAsync<
                ValidationException>(
                () => service.GetAllAsync(Guid.Empty));

            Assert.Equal(
                "INVALID_USER_ID",
                invalidUser.Message);

            var invalidAddress = await Assert.ThrowsAsync<
                ValidationException>(
                () => service.GetByIdAsync(
                    owner.Id,
                    Guid.Empty));

            Assert.Equal(
                "INVALID_ADDRESS_ID",
                invalidAddress.Message);

            // 9. Queries must not track entities.
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static User NewUser(string label)
    {
        return new User
        {
            FullName = $"{label} Customer",
            Email =
                $"address-query-{Guid.NewGuid():N}@example.invalid",
            Role = UserRole.Customer,
            IsActive = true
        };
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
}
