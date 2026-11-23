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

public sealed class UserAddressSetDefaultServiceTests
{
    [Fact]
    public async Task SetDefault_ShouldSwitchAndBeIdempotent()
    {
        var connection = GetTestConnection();
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserAddressService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var owner = NewCustomer(hasher);
        var other = NewCustomer(hasher);
        var now = DateTime.UtcNow.AddDays(-3);
        var previous = NewAddress(owner.Id, "Previous", now, true);
        var target = NewAddress(owner.Id, "Target", now.AddDays(1));
        var extra = NewAddress(owner.Id, "Extra", now.AddDays(2));
        var privateAddress = NewAddress(other.Id, "Private", now, true);
        var userIds = new[] { owner.Id, other.Id };

        try
        {
            db.Users.AddRange(owner, other);
            db.UserAddresses.AddRange(previous, target, extra, privateAddress);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await service.SetDefaultAsync(owner.Id, target.Id);

            Assert.Equal(target.Id, result.AddressId);
            Assert.True(result.IsDefault);
            Assert.NotNull(result.UpdatedAtUtc);

            var firstState = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == owner.Id).ToListAsync();

            Assert.Equal(3, firstState.Count);
            Assert.Equal(target.Id, Assert.Single(firstState, x => x.IsDefault).Id);
            Assert.False(Assert.Single(firstState, x => x.Id == previous.Id).IsDefault);
            Assert.False(Assert.Single(firstState, x => x.Id == extra.Id).IsDefault);

            var targetTimestamp = Assert.Single(firstState, x => x.Id == target.Id).UpdatedAt;
            var previousTimestamp = Assert.Single(firstState, x => x.Id == previous.Id).UpdatedAt;
            Assert.NotNull(targetTimestamp);
            Assert.NotNull(previousTimestamp);

            // Idempotency: no mutation if the same address is selected again.
            var repeated = await service.SetDefaultAsync(owner.Id, target.Id);
            Assert.True(repeated.IsDefault);
            Assert.Equal(target.Id, repeated.AddressId);

            var secondState = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == owner.Id).ToListAsync();

            Assert.Equal(targetTimestamp,
                Assert.Single(secondState, x => x.Id == target.Id).UpdatedAt);
            Assert.Equal(previousTimestamp,
                Assert.Single(secondState, x => x.Id == previous.Id).UpdatedAt);
            Assert.Equal(target.Id,
                Assert.Single(secondState, x => x.IsDefault).Id);

            // Other Customer's default is untouched.
            var otherStored = await db.UserAddresses.AsNoTracking()
                .SingleAsync(x => x.Id == privateAddress.Id);
            Assert.True(otherStored.IsDefault);
            Assert.Equal(other.Id, otherStored.UserId);
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
    public async Task SetDefault_ShouldRepairMissingDefault()
    {
        var connection = GetTestConnection();
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserAddressService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewCustomer(hasher);
        var first = NewAddress(owner.Id, "First", DateTime.UtcNow.AddDays(-2));
        var target = NewAddress(owner.Id, "Target", DateTime.UtcNow.AddDays(-1));

        try
        {
            db.Users.Add(owner);
            db.UserAddresses.AddRange(first, target);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await service.SetDefaultAsync(owner.Id, target.Id);
            Assert.True(result.IsDefault);

            var stored = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == owner.Id).ToListAsync();

            Assert.Equal(2, stored.Count);
            Assert.Equal(target.Id, Assert.Single(stored, x => x.IsDefault).Id);
            Assert.Null(Assert.Single(stored, x => x.Id == first.Id).UpdatedAt);
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

    [Fact]
    public async Task SetDefault_ShouldEnforceOwnershipAndAccountState()
    {
        var connection = GetTestConnection();
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserAddressService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var owner = NewCustomer(hasher);
        var other = NewCustomer(hasher);
        var inactive = NewCustomer(hasher, isActive: false);
        var admin = NewCustomer(hasher, role: UserRole.Admin);
        var userIds = new[] { owner.Id, other.Id, inactive.Id, admin.Id };
        var ownerAddress = NewAddress(owner.Id, "Owner", DateTime.UtcNow, true);
        var otherAddress = NewAddress(other.Id, "Other", DateTime.UtcNow, true);

        try
        {
            db.Users.AddRange(owner, other, inactive, admin);
            db.UserAddresses.AddRange(ownerAddress, otherAddress);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var forbidden = await Assert.ThrowsAsync<BusinessException>(
                () => service.SetDefaultAsync(owner.Id, otherAddress.Id));
            Assert.Equal(BusinessErrorType.NotFound, forbidden.ErrorType);
            Assert.Equal("ADDRESS_NOT_FOUND", forbidden.Message);

            var missing = await Assert.ThrowsAsync<BusinessException>(
                () => service.SetDefaultAsync(owner.Id, Guid.NewGuid()));
            Assert.Equal("ADDRESS_NOT_FOUND", missing.Message);

            var inactiveError = await Assert.ThrowsAsync<BusinessException>(
                () => service.SetDefaultAsync(inactive.Id, ownerAddress.Id));
            Assert.Equal("USER_NOT_FOUND", inactiveError.Message);

            var adminError = await Assert.ThrowsAsync<BusinessException>(
                () => service.SetDefaultAsync(admin.Id, ownerAddress.Id));
            Assert.Equal("USER_NOT_FOUND", adminError.Message);

            var invalidUser = await Assert.ThrowsAsync<ValidationException>(
                () => service.SetDefaultAsync(Guid.Empty, ownerAddress.Id));
            Assert.Equal("INVALID_USER_ID", invalidUser.Message);

            var invalidAddress = await Assert.ThrowsAsync<ValidationException>(
                () => service.SetDefaultAsync(owner.Id, Guid.Empty));
            Assert.Equal("INVALID_ADDRESS_ID", invalidAddress.Message);

            var stillOwner = await db.UserAddresses.AsNoTracking()
                .SingleAsync(x => x.Id == ownerAddress.Id);
            var stillOther = await db.UserAddresses.AsNoTracking()
                .SingleAsync(x => x.Id == otherAddress.Id);
            Assert.True(stillOwner.IsDefault);
            Assert.True(stillOther.IsDefault);
            Assert.Null(stillOwner.UpdatedAt);
            Assert.Null(stillOther.UpdatedAt);
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
    public async Task ConcurrentSetDefault_ShouldKeepExactlyOneDefault()
    {
        var connection = GetTestConnection();
        using var factory = new TestApiFactory(connection);
        using var seedScope = factory.Services.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = seedScope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewCustomer(hasher);
        var original = NewAddress(owner.Id, "Original", DateTime.UtcNow.AddDays(-3), true);
        var alpha = NewAddress(owner.Id, "Alpha", DateTime.UtcNow.AddDays(-2));
        var beta = NewAddress(owner.Id, "Beta", DateTime.UtcNow.AddDays(-1));

        try
        {
            db.Users.Add(owner);
            db.UserAddresses.AddRange(original, alpha, beta);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var firstScope = factory.Services.CreateScope();
            using var secondScope = factory.Services.CreateScope();
            var first = firstScope.ServiceProvider.GetRequiredService<IUserAddressService>();
            var second = secondScope.ServiceProvider.GetRequiredService<IUserAddressService>();

            var results = await Task.WhenAll(
                first.SetDefaultAsync(owner.Id, alpha.Id),
                second.SetDefaultAsync(owner.Id, beta.Id));

            Assert.Equal(2, results.Length);
            Assert.All(results, result => Assert.True(result.IsDefault));

            var stored = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == owner.Id).ToListAsync();
            Assert.Equal(3, stored.Count);
            var chosen = Assert.Single(stored, x => x.IsDefault);
            Assert.True(chosen.Id == alpha.Id || chosen.Id == beta.Id);
            Assert.False(Assert.Single(stored, x => x.Id == original.Id).IsDefault);
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

    [Fact]
    public async Task ConcurrentSetDefaultAndDelete_ShouldPreserveDefault()
    {
        var connection = GetTestConnection();
        using var factory = new TestApiFactory(connection);
        using var seedScope = factory.Services.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = seedScope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewCustomer(hasher);
        var original = NewAddress(owner.Id, "Original", DateTime.UtcNow.AddDays(-3), true);
        var target = NewAddress(owner.Id, "Target", DateTime.UtcNow.AddDays(-2));
        var backup = NewAddress(owner.Id, "Backup", DateTime.UtcNow.AddDays(-1));

        try
        {
            db.Users.Add(owner);
            db.UserAddresses.AddRange(original, target, backup);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var setScope = factory.Services.CreateScope();
            using var deleteScope = factory.Services.CreateScope();
            var setService = setScope.ServiceProvider.GetRequiredService<IUserAddressService>();
            var deleteService = deleteScope.ServiceProvider.GetRequiredService<IUserAddressService>();

            var setTask = setService.SetDefaultAsync(owner.Id, target.Id);
            var deleteTask = deleteService.DeleteAsync(owner.Id, original.Id);
            await Task.WhenAll(setTask, deleteTask);

            Assert.True((await setTask).IsDefault);
            var stored = await db.UserAddresses.AsNoTracking()
                .Where(x => x.UserId == owner.Id).ToListAsync();

            Assert.Equal(2, stored.Count);
            Assert.DoesNotContain(stored, x => x.Id == original.Id);
            Assert.Equal(target.Id, Assert.Single(stored, x => x.IsDefault).Id);
            Assert.False(Assert.Single(stored, x => x.Id == backup.Id).IsDefault);
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

    private static User NewCustomer(
        IPasswordHasher<User> hasher,
        UserRole role = UserRole.Customer,
        bool isActive = true)
    {
        var user = new User
        {
            FullName = "Address Default Customer",
            Email = $"address-default-{Guid.NewGuid():N}@example.invalid",
            Role = role,
            IsActive = isActive
        };
        user.PasswordHash = hasher.HashPassword(user, "FlowerShop#2026");
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
