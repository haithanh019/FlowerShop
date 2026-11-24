using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.UserProfiles;
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

public sealed class UserProfileUpdateServiceTests
{
    [Fact]
    public async Task Update_ShouldNormalizePreserveProtectedFieldsAndBeIdempotent()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserProfileService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewUser(hasher);
        owner.IsEmailVerified = true;
        owner.PhoneNumber = "0901234567";
        var other = NewUser(hasher);
        Guid[] userIds = [owner.Id, other.Id];

        try
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var before = await db.Users.AsNoTracking().SingleAsync(x => x.Id == owner.Id);
            var otherBefore = await db.Users.AsNoTracking().SingleAsync(x => x.Id == other.Id);

            // An unchanged profile must not create an UpdatedAt timestamp.
            var unchanged = await service.UpdateAsync(owner.Id,
                new UpdateUserProfileDto
                {
                    FullName = $"  {before.FullName}  ",
                    PhoneNumber = $" {before.PhoneNumber} "
                });
            Assert.Null(unchanged.UpdatedAtUtc);
            db.ChangeTracker.Clear();

            var updated = await service.UpdateAsync(owner.Id,
                new UpdateUserProfileDto
                {
                    FullName = "  Updated Customer  ",
                    PhoneNumber = " +84 901 234 567 "
                });
            Assert.Equal(owner.Id, updated.UserId);
            Assert.Equal("Updated Customer", updated.FullName);
            Assert.Equal("+84 901 234 567", updated.PhoneNumber);
            Assert.Equal(before.Email, updated.Email);
            Assert.True(updated.IsEmailVerified);
            Assert.NotNull(updated.UpdatedAtUtc);
            Assert.Equal<DateTimeKind?>(DateTimeKind.Utc, updated.UpdatedAtUtc?.Kind);
            Assert.Equal(before.CreatedAt, updated.CreatedAtUtc);

            var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == owner.Id);
            Assert.Equal(updated.FullName, stored.FullName);
            Assert.Equal(updated.PhoneNumber, stored.PhoneNumber);
            Assert.Equal(updated.UpdatedAtUtc, stored.UpdatedAt);
            AssertProtectedFields(before, stored);

            db.ChangeTracker.Clear();
            var repeated = await service.UpdateAsync(owner.Id,
                new UpdateUserProfileDto
                {
                    FullName = " Updated Customer ",
                    PhoneNumber = " +84 901 234 567 "
                });
            Assert.Equal(updated, repeated);
            var repeatedStored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == owner.Id);
            Assert.Equal(stored.UpdatedAt, repeatedStored.UpdatedAt);
            AssertProtectedFields(before, repeatedStored);

            db.ChangeTracker.Clear();
            var cleared = await service.UpdateAsync(owner.Id,
                new UpdateUserProfileDto { FullName = "Updated Customer", PhoneNumber = "   " });
            Assert.Null(cleared.PhoneNumber);
            var clearedStored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == owner.Id);
            Assert.Null(clearedStored.PhoneNumber);
            Assert.NotNull(clearedStored.UpdatedAt);
            AssertProtectedFields(before, clearedStored);

            db.ChangeTracker.Clear();
            var read = await service.GetAsync(owner.Id);
            Assert.Equal(cleared, read);
            Assert.Empty(db.ChangeTracker.Entries());

            var otherAfter = await db.Users.AsNoTracking().SingleAsync(x => x.Id == other.Id);
            AssertProtectedFields(otherBefore, otherAfter);
            Assert.Equal(otherBefore.FullName, otherAfter.FullName);
            Assert.Equal(otherBefore.PhoneNumber, otherAfter.PhoneNumber);
            Assert.Equal(otherBefore.UpdatedAt, otherAfter.UpdatedAt);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Users.Where(x => userIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Update_ShouldRejectInvalidMissingInactiveAndAdminUsersWithoutMutation()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserProfileService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewUser(hasher);
        var inactive = NewUser(hasher);
        inactive.IsActive = false;
        var admin = NewUser(hasher);
        admin.Role = UserRole.Admin;
        Guid[] userIds = [owner.Id, inactive.Id, admin.Id];
        var request = new UpdateUserProfileDto
        {
            FullName = "Changed Customer",
            PhoneNumber = "0901234567"
        };

        try
        {
            db.Users.AddRange(owner, inactive, admin);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await db.Users.AsNoTracking()
                .Where(x => userIds.Contains(x.Id)).ToListAsync();

            var invalidId = await Assert.ThrowsAsync<ValidationException>(
                () => service.UpdateAsync(Guid.Empty, request));
            Assert.Equal("INVALID_USER_ID", invalidId.Message);

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.UpdateAsync(owner.Id, null!));

            var invalidName = await Assert.ThrowsAsync<ValidationException>(
                () => service.UpdateAsync(owner.Id,
                    new UpdateUserProfileDto { FullName = " " }));
            Assert.Equal("FULL_NAME_REQUIRED", invalidName.Message);

            var invalidPhone = await Assert.ThrowsAsync<ValidationException>(
                () => service.UpdateAsync(owner.Id,
                    new UpdateUserProfileDto { FullName = "Valid Name", PhoneNumber = "abc" }));
            Assert.Equal("PHONE_NUMBER_INVALID", invalidPhone.Message);

            Guid[] rejectedIds = [Guid.NewGuid(), inactive.Id, admin.Id];
            foreach (var userId in rejectedIds)
            {
                var error = await Assert.ThrowsAsync<BusinessException>(
                    () => service.UpdateAsync(userId, request));
                Assert.Equal(BusinessErrorType.NotFound, error.ErrorType);
                Assert.Equal("USER_NOT_FOUND", error.Message);
            }

            var after = await db.Users.AsNoTracking()
                .Where(x => userIds.Contains(x.Id)).ToListAsync();
            Assert.Equal(3, after.Count);
            foreach (var original in before)
            {
                var current = Assert.Single(after, x => x.Id == original.Id);
                AssertProtectedFields(original, current);
                Assert.Equal(original.FullName, current.FullName);
                Assert.Equal(original.PhoneNumber, current.PhoneNumber);
                Assert.Equal(original.UpdatedAt, current.UpdatedAt);
            }
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Users.Where(x => userIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ConcurrentProfileUpdate_ShouldPersistOneCompletePayload()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var seedScope = factory.Services.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = seedScope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var owner = NewUser(hasher);
        owner.PhoneNumber = "0901234567";

        try
        {
            db.Users.Add(owner);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await db.Users.AsNoTracking().SingleAsync(x => x.Id == owner.Id);

            using var firstScope = factory.Services.CreateScope();
            using var secondScope = factory.Services.CreateScope();
            var firstService = firstScope.ServiceProvider.GetRequiredService<IUserProfileService>();
            var secondService = secondScope.ServiceProvider.GetRequiredService<IUserProfileService>();

            // Each request retains one ORIGINAL field. Without serialization,
            // EF's per-property updates could accidentally merge the two payloads.
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

            var results = await Task.WhenAll(
                firstService.UpdateAsync(owner.Id, first),
                secondService.UpdateAsync(owner.Id, second));

            Assert.Equal(first.FullName, results[0].FullName);
            Assert.Equal(first.PhoneNumber, results[0].PhoneNumber);
            Assert.Equal(second.FullName, results[1].FullName);
            Assert.Equal(second.PhoneNumber, results[1].PhoneNumber);

            var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == owner.Id);
            Assert.True(
                (stored.FullName == first.FullName && stored.PhoneNumber == first.PhoneNumber) ||
                (stored.FullName == second.FullName && stored.PhoneNumber == second.PhoneNumber),
                "Final persisted profile must match one complete request.");
            Assert.NotNull(stored.UpdatedAt);
            AssertProtectedFields(before, stored);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Users.Where(x => x.Id == owner.Id).ExecuteDeleteAsync();
        }
    }

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

    private static User NewUser(IPasswordHasher<User> hasher)
    {
        var user = new User
        {
            FullName = "Original Customer",
            Email = $"profile-update-{Guid.NewGuid():N}@example.invalid",
            Role = UserRole.Customer,
            IsActive = true,
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)
        };
        user.PasswordHash = hasher.HashPassword(user, "FlowerShop#2026");
        return user;
    }

    private static string GetTestConnection()
    {
        var connection = Environment.GetEnvironmentVariable(
            "FLOWERSHOP_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");

        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase) || sql.MultipleActiveResultSets)
            throw new InvalidOperationException("Use FlowerShop_Test with MARS disabled.");
        return connection;
    }
}
