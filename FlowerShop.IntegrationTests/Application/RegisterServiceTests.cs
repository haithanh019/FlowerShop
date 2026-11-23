using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Auth;
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

public sealed class RegisterServiceTests
{
    [Fact]
    public async Task Register_ShouldCreateCustomerAndRejectDuplicateEmail()
    {
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

        var authService = scope.ServiceProvider
            .GetRequiredService<IAuthService>();

        var hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<User>>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            x => x.EndsWith("_InitialCreate"));

        const string password = "FlowerShop#2026";

        var email =
            $"register-{Guid.NewGuid():N}@example.invalid";

        var request = new RegisterDto
        {
            FullName = "  Nguyen Van A  ",
            Email = $"  {email.ToUpperInvariant()}  ",
            Password = password,
            ConfirmPassword = password
        };

        try
        {
            // 1. Register a new Customer.
            var result = await authService.RegisterAsync(request);

            Assert.NotEqual(Guid.Empty, result.UserId);
            Assert.Equal("Nguyen Van A", result.FullName);
            Assert.Equal(email, result.Email);

            // 2. Verify persisted User.
            var storedUser = await db.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == result.UserId);

            Assert.Equal(UserRole.Customer, storedUser.Role);
            Assert.True(storedUser.IsActive);
            Assert.False(storedUser.IsEmailVerified);
            Assert.Equal(email, storedUser.Email);
            Assert.Equal("Nguyen Van A", storedUser.FullName);

            // 3. Verify password is hashed, not stored as text.
            Assert.NotEqual(password, storedUser.PasswordHash);
            Assert.False(string.IsNullOrWhiteSpace(
                storedUser.PasswordHash));

            var verification = hasher.VerifyHashedPassword(
                storedUser,
                storedUser.PasswordHash,
                password);

            Assert.NotEqual(
                PasswordVerificationResult.Failed,
                verification);

            // 4. A new Customer can log in.
            var login = await authService.LoginAsync(
                new LoginDto
                {
                    Email = email.ToUpperInvariant(),
                    Password = password
                });

            Assert.False(
                string.IsNullOrWhiteSpace(login.AccessToken));

            // 5. Duplicate email must be rejected.
            var duplicateRequest = new RegisterDto
            {
                FullName = "Another Customer",
                Email = email,
                Password = "AnotherPassword#2026",
                ConfirmPassword = "AnotherPassword#2026"
            };

            var duplicate = await Assert.ThrowsAsync<
                BusinessException>(
                () => authService.RegisterAsync(
                    duplicateRequest));

            Assert.Equal(
                BusinessErrorType.Conflict,
                duplicate.ErrorType);

            Assert.Equal(
                "EMAIL_ALREADY_REGISTERED",
                duplicate.Message);

            // 6. Database must still contain exactly one User.
            Assert.Equal(
                1,
                await db.Users.AsNoTracking()
                    .CountAsync(x => x.Email == email));

            // 7. Invalid confirmation must fail validation.
            var invalidRequest = new RegisterDto
            {
                FullName = "Invalid Customer",
                Email = $"other-{Guid.NewGuid():N}@example.invalid",
                Password = password,
                ConfirmPassword = "DifferentPassword#2026"
            };

            await Assert.ThrowsAsync<ValidationException>(
                () => authService.RegisterAsync(
                    invalidRequest));
        }
        finally
        {
            // Only remove the User created by this test.
            db.ChangeTracker.Clear();

            await db.Users
                .Where(x => x.Email == email)
                .ExecuteDeleteAsync();
        }
    }
}
