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

public sealed class UserProfileQueryServiceTests
{
    [Fact]
    public async Task Get_ShouldReturnCustomerProfileWithoutTracking()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserProfileService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        // Use exact whole-second timestamps to avoid SQL precision rounding.
        var createdAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var updatedAt = createdAt.AddDays(1);
        var owner = NewUser(hasher, "Owner");
        owner.PhoneNumber = "+84 901 234 567";
        owner.IsEmailVerified = true;
        owner.CreatedAt = createdAt;
        owner.UpdatedAt = updatedAt;

        var other = NewUser(hasher, "Other");
        other.CreatedAt = createdAt;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var profile = await service.GetAsync(owner.Id);
            Assert.Equal(owner.Id, profile.UserId);
            Assert.Equal(owner.FullName, profile.FullName);
            Assert.Equal(owner.Email, profile.Email);
            Assert.Equal(owner.PhoneNumber, profile.PhoneNumber);
            Assert.True(profile.IsEmailVerified);
            Assert.Equal(createdAt, profile.CreatedAtUtc);
            Assert.Equal(DateTimeKind.Utc, profile.CreatedAtUtc.Kind);
            Assert.Equal<DateTime?>(updatedAt, profile.UpdatedAtUtc);
            Assert.Equal<DateTimeKind?>(DateTimeKind.Utc, profile.UpdatedAtUtc?.Kind);

            // The second user has independent profile data and nullable fields.
            var otherProfile = await service.GetAsync(other.Id);
            Assert.Equal(other.Id, otherProfile.UserId);
            Assert.Equal(other.Email, otherProfile.Email);
            Assert.NotEqual(profile.UserId, otherProfile.UserId);
            Assert.Null(otherProfile.PhoneNumber);
            Assert.Null(otherProfile.UpdatedAtUtc);
            Assert.False(otherProfile.IsEmailVerified);

            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Get_ShouldRejectInvalidMissingInactiveAndAdminUsers()
    {
        using var factory = new TestApiFactory(GetTestConnection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUserProfileService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var inactive = NewUser(hasher, "Inactive");
        inactive.IsActive = false;
        var admin = NewUser(hasher, "Admin");
        admin.Role = UserRole.Admin;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            db.Users.AddRange(inactive, admin);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var invalid = await Assert.ThrowsAsync<ValidationException>(
                () => service.GetAsync(Guid.Empty));
            Assert.Equal("INVALID_USER_ID", invalid.Message);

            Guid[] rejectedIds = [Guid.NewGuid(), inactive.Id, admin.Id];
            foreach (var userId in rejectedIds)
            {
                var error = await Assert.ThrowsAsync<BusinessException>(
                    () => service.GetAsync(userId));
                Assert.Equal(BusinessErrorType.NotFound, error.ErrorType);
                Assert.Equal("USER_NOT_FOUND", error.Message);
            }

            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static User NewUser(IPasswordHasher<User> hasher, string label)
    {
        var user = new User
        {
            FullName = $"{label} Profile Customer",
            Email = $"profile-query-{Guid.NewGuid():N}@example.invalid",
            Role = UserRole.Customer,
            IsActive = true
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
