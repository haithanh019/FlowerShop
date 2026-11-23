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

public sealed class UserAddressCreateServiceTests
{
    [Fact]
    public async Task Create_ShouldNormalizeSetDefaultAndValidateCustomer()
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

        var owner = NewCustomer(hasher);
        var other = NewCustomer(hasher);
        var inactive = NewCustomer(hasher);
        inactive.IsActive = false;

        var admin = NewCustomer(hasher);
        admin.Role = UserRole.Admin;

        var userIds = new[]
        {
            owner.Id, other.Id, inactive.Id, admin.Id
        };

        try
        {
            db.Users.AddRange(owner, other, inactive, admin);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // First address: automatically default.
            var firstRequest = ValidAddress();
            firstRequest.PostalCode = "   ";

            var first = await service.CreateAsync(
                owner.Id, firstRequest);

            Assert.NotEqual(Guid.Empty, first.AddressId);
            Assert.True(first.IsDefault);
            Assert.Equal("Nguyen Van A", first.RecipientName);
            Assert.Equal("090 123 4567", first.RecipientPhone);
            Assert.Equal("VN", first.CountryCode);
            Assert.Null(first.PostalCode);

            // Verify the address was committed.
            var stored = await db.UserAddresses
                .AsNoTracking()
                .SingleAsync(x => x.Id == first.AddressId);

            Assert.True(stored.IsDefault);
            Assert.Equal(owner.Id, stored.UserId);

            // Second address: must not replace default.
            var secondRequest = ValidAddress();
            secondRequest.RecipientName = "Second Recipient";
            secondRequest.PostalCode = " 550000 ";

            var second = await service.CreateAsync(
                owner.Id, secondRequest);

            Assert.False(second.IsDefault);
            Assert.Equal("550000", second.PostalCode);

            // Another Customer has an independent default.
            var otherAddress = await service.CreateAsync(
                other.Id, ValidAddress());

            Assert.True(otherAddress.IsDefault);

            var ownerAddresses = await service.GetAllAsync(
                owner.Id);

            Assert.Equal(2, ownerAddresses.Count);
            Assert.Equal(
                first.AddressId,
                ownerAddresses[0].AddressId);

            Assert.Single(
                ownerAddresses,
                x => x.IsDefault);

            Assert.DoesNotContain(
                ownerAddresses,
                x => x.AddressId == otherAddress.AddressId);

            // Invalid DTO: reject before saving.
            var invalidDto = ValidAddress();
            invalidDto.RecipientPhone = "abc";

            await Assert.ThrowsAsync<ValidationException>(
                () => service.CreateAsync(
                    owner.Id, invalidDto));

            // Invalid UserId.
            await Assert.ThrowsAsync<ValidationException>(
                () => service.CreateAsync(
                    Guid.Empty, ValidAddress()));

            // Nonexistent User.
            var missing = await Assert.ThrowsAsync<
                BusinessException>(
                () => service.CreateAsync(
                    Guid.NewGuid(), ValidAddress()));

            Assert.Equal(
                BusinessErrorType.NotFound,
                missing.ErrorType);

            Assert.Equal(
                "USER_NOT_FOUND",
                missing.Message);

            // Inactive Customer cannot create addresses.
            await Assert.ThrowsAsync<BusinessException>(
                () => service.CreateAsync(
                    inactive.Id, ValidAddress()));

            // Admin cannot use the Customer address service.
            await Assert.ThrowsAsync<BusinessException>(
                () => service.CreateAsync(
                    admin.Id, ValidAddress()));

            // Invalid requests must not create more records.
            Assert.Equal(
                2,
                await db.UserAddresses.AsNoTracking()
                    .CountAsync(x => x.UserId == owner.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();

            // Delete addresses before users (FK: NoAction).
            await db.UserAddresses
                .Where(x => userIds.Contains(x.UserId))
                .ExecuteDeleteAsync();

            await db.Users
                .Where(x => userIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ConcurrentCreate_ShouldPersistTwoAddressesWithOneDefault()
    {
        var connection = GetTestConnection();

        using var factory = new TestApiFactory(connection);

        using var seedScope = factory.Services.CreateScope();

        var db = seedScope.ServiceProvider
            .GetRequiredService<FlowerShopDbContext>();

        var hasher = seedScope.ServiceProvider
            .GetRequiredService<IPasswordHasher<User>>();

        var owner = NewCustomer(hasher);

        try
        {
            db.Users.Add(owner);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Separate scopes = separate DbContext instances.
            using var firstScope = factory.Services.CreateScope();
            using var secondScope = factory.Services.CreateScope();

            var firstService = firstScope.ServiceProvider
                .GetRequiredService<IUserAddressService>();

            var secondService = secondScope.ServiceProvider
                .GetRequiredService<IUserAddressService>();

            var firstRequest = ValidAddress();
            firstRequest.RecipientName = "First Recipient";

            var secondRequest = ValidAddress();
            secondRequest.RecipientName = "Second Recipient";

            // Both operations start without awaiting the other.
            var firstTask = firstService.CreateAsync(
                owner.Id, firstRequest);

            var secondTask = secondService.CreateAsync(
                owner.Id, secondRequest);

            var results = await Task.WhenAll(
                firstTask, secondTask);

            Assert.Equal(2, results.Length);

            Assert.NotEqual(
                results[0].AddressId,
                results[1].AddressId);

            // Exactly one result is the default address.
            Assert.Single(
                results,
                x => x.IsDefault);

            // Verify the committed database state.
            var stored = await db.UserAddresses
                .AsNoTracking()
                .Where(x => x.UserId == owner.Id)
                .ToListAsync();

            Assert.Equal(2, stored.Count);

            Assert.Single(
                stored,
                x => x.IsDefault);

            Assert.All(stored, x =>
                Assert.Equal(owner.Id, x.UserId));
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

    private static User NewCustomer(
        IPasswordHasher<User> hasher)
    {
        var user = new User
        {
            FullName = "Address Test Customer",
            Email =
                $"address-create-{Guid.NewGuid():N}@example.invalid",
            Role = UserRole.Customer,
            IsActive = true
        };

        user.PasswordHash = hasher.HashPassword(
            user,
            "FlowerShop#2026");

        return user;
    }

    private static UserAddressUpsertDto ValidAddress()
    {
        return new UserAddressUpsertDto
        {
            RecipientName = "  Nguyen Van A  ",
            RecipientPhone = " 090 123 4567 ",
            AddressLine = " 123 Nguyen Van Linh ",
            Ward = " Hai Chau ",
            District = " Hai Chau ",
            Province = " Da Nang ",
            CountryCode = " vn ",
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
