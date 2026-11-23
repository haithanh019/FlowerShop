
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

public sealed class UserAddressDeleteServiceTests
{
    [Fact]
    public async Task Delete_ShouldPromoteOldestAddressAndHandleLastAddress()
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

        var now = DateTime.UtcNow;

        var defaultAddress = NewAddress(
            owner.Id, "Default", now.AddDays(-3), true);

        var older = NewAddress(
            owner.Id, "Older", now.AddDays(-2));

        var newer = NewAddress(
            owner.Id, "Newer", now.AddDays(-1));

        var otherDefault = NewAddress(
            other.Id, "Private", now, true);

        var userIds = new[] { owner.Id, other.Id };

        try
        {
            db.Users.AddRange(owner, other);
            db.UserAddresses.AddRange(
                defaultAddress, older, newer, otherDefault);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // 1. Delete a non-default address.
            await service.DeleteAsync(owner.Id, newer.Id);

            Assert.False(
                await db.UserAddresses.AsNoTracking()
                    .AnyAsync(x => x.Id == newer.Id));

            var currentDefault = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x =>
                    x.UserId == owner.Id && x.IsDefault);

            Assert.Equal(defaultAddress.Id, currentDefault.Id);

            // 2. Delete the current default address.
            await service.DeleteAsync(
                owner.Id, defaultAddress.Id);

            Assert.False(
                await db.UserAddresses.AsNoTracking()
                    .AnyAsync(x => x.Id == defaultAddress.Id));

            var promoted = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.UserId == owner.Id);

            Assert.Equal(older.Id, promoted.Id);
            Assert.True(promoted.IsDefault);
            Assert.NotNull(promoted.UpdatedAt);

            // 3. Delete the last address.
            await service.DeleteAsync(owner.Id, older.Id);

            Assert.Empty(
                await db.UserAddresses.AsNoTracking()
                    .Where(x => x.UserId == owner.Id)
                    .ToListAsync());

            // 4. The other Customer remains untouched.
            var privateAddress = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.Id == otherDefault.Id);

            Assert.Equal(other.Id, privateAddress.UserId);
            Assert.True(privateAddress.IsDefault);
            Assert.Equal(
                "Private Recipient",
                privateAddress.RecipientName);
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
    public async Task Delete_ShouldUseAddressIdAsTieBreaker()
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
        var createdAt = DateTime.UtcNow.AddDays(-1);

        var original = NewAddress(
            owner.Id, "Default", createdAt, true);

        var candidateA = NewAddress(
            owner.Id, "Candidate A", createdAt);

        var candidateB = NewAddress(
            owner.Id, "Candidate B", createdAt);

        var expectedId = new[]
        {
            candidateA.Id,
            candidateB.Id
        }.OrderBy(x => x).First();

        try
        {
            db.Users.Add(owner);
            db.UserAddresses.AddRange(
                original, candidateA, candidateB);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await service.DeleteAsync(owner.Id, original.Id);

            var remaining = await db.UserAddresses
                .AsNoTracking()
                .Where(x => x.UserId == owner.Id)
                .ToListAsync();

            Assert.Equal(2, remaining.Count);

            var selected = Assert.Single(
                remaining,
                x => x.IsDefault);

            Assert.Equal(expectedId, selected.Id);
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

    [Fact]
    public async Task Delete_ShouldEnforceOwnershipAndAccountState()
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
        var inactive = NewUser(hasher, isActive: false);
        var admin = NewUser(hasher, role: UserRole.Admin);

        var address = NewAddress(
            owner.Id, "Owner", DateTime.UtcNow, true);

        var userIds = new[]
        {
            owner.Id, other.Id, inactive.Id, admin.Id
        };

        try
        {
            db.Users.AddRange(owner, other, inactive, admin);
            db.UserAddresses.Add(address);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Another Customer cannot delete this address.
            var forbidden = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.DeleteAsync(
                    other.Id, address.Id));

            Assert.Equal(
                BusinessErrorType.NotFound,
                forbidden.ErrorType);

            Assert.Equal(
                "ADDRESS_NOT_FOUND",
                forbidden.Message);

            // Nonexistent address.
            var missing = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.DeleteAsync(
                    owner.Id, Guid.NewGuid()));

            Assert.Equal(
                "ADDRESS_NOT_FOUND",
                missing.Message);

            // Invalid identifiers.
            await Assert.ThrowsAsync<ValidationException>(
                () => service.DeleteAsync(
                    Guid.Empty, address.Id));

            await Assert.ThrowsAsync<ValidationException>(
                () => service.DeleteAsync(
                    owner.Id, Guid.Empty));

            // Disabled Customer.
            var inactiveError = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.DeleteAsync(
                    inactive.Id, address.Id));

            Assert.Equal(
                "USER_NOT_FOUND",
                inactiveError.Message);

            // Admin cannot use the Customer service.
            var adminError = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.DeleteAsync(
                    admin.Id, address.Id));

            Assert.Equal(
                "USER_NOT_FOUND",
                adminError.Message);

            // All rejected requests must preserve the data.
            var stored = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.Id == address.Id);

            Assert.Equal(owner.Id, stored.UserId);
            Assert.True(stored.IsDefault);
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
    public async Task ConcurrentDeleteAndCreate_ShouldKeepOneDefault()
    {
        var connection = GetTestConnection();

        using var factory = new TestApiFactory(connection);
        using var seedScope = factory.Services.CreateScope();

        var db = seedScope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var hasher = seedScope.ServiceProvider
            .GetRequiredService<IPasswordHasher<User>>();

        var owner = NewUser(hasher);

        var oldDefault = NewAddress(
            owner.Id,
            "Old Default",
            DateTime.UtcNow.AddDays(-2),
            true);

        var secondary = NewAddress(
            owner.Id,
            "Secondary",
            DateTime.UtcNow.AddDays(-1));

        try
        {
            db.Users.Add(owner);

            db.UserAddresses.AddRange(
                oldDefault, secondary);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Separate scopes and separate DbContexts.
            using var deleteScope =
                factory.Services.CreateScope();

            using var createScope =
                factory.Services.CreateScope();

            var deleteService = deleteScope.ServiceProvider
                .GetRequiredService<IUserAddressService>();

            var createService = createScope.ServiceProvider
                .GetRequiredService<IUserAddressService>();

            // Delete and Create use the same user-scoped lock.
            var deleteTask = deleteService.DeleteAsync(
                owner.Id, oldDefault.Id);

            var createTask = createService.CreateAsync(
                owner.Id, ValidAddress());

            await Task.WhenAll(deleteTask, createTask);

            var created = await createTask;

            Assert.False(created.IsDefault);

            var remaining = await db.UserAddresses
                .AsNoTracking()
                .Where(x => x.UserId == owner.Id)
                .ToListAsync();

            Assert.Equal(2, remaining.Count);

            Assert.DoesNotContain(
                remaining,
                x => x.Id == oldDefault.Id);

            Assert.Contains(
                remaining,
                x => x.Id == created.AddressId);

            var selected = Assert.Single(
                remaining,
                x => x.IsDefault);

            Assert.Equal(secondary.Id, selected.Id);
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
        IPasswordHasher<User> hasher,
        UserRole role = UserRole.Customer,
        bool isActive = true)
    {
        var user = new User
        {
            FullName = "Address Delete Customer",
            Email =
                $"address-delete-{Guid.NewGuid():N}@example.invalid",
            Role = role,
            IsActive = isActive
        };

        user.PasswordHash = hasher.HashPassword(
            user, "FlowerShop#2026");

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
            RecipientName = "New Recipient",
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
