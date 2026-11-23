using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Identity;

namespace FlowerShop.Application.Services;

public sealed class AuthService(
    IGenericRepository<User> users,
    IUnitOfWork unitOfWork,
    IPasswordHasher<User> passwordHasher,
    IJwtTokenService jwtTokenService) : IAuthService
{
    public async Task<LoginResponseDto> LoginAsync(
        LoginDto dto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto,
            new ValidationContext(dto),
            validateAllProperties: true);

        // Match the normalized emails created by Register.
        var email = NormalizeEmail(dto.Email);

        var user = await users.GetByAsync(
            x => x.Email == email,
            cancellationToken: cancellationToken);

        if (user is null || !user.IsActive)
            throw InvalidCredentials();

        var result = passwordHasher.VerifyHashedPassword(
            user,
            user.PasswordHash,
            dto.Password);

        if (result == PasswordVerificationResult.Failed)
            throw InvalidCredentials();

        return jwtTokenService.CreateToken(user);
    }

    public async Task<RegisterResponseDto> RegisterAsync(
        RegisterDto dto,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate input.
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto,
            new ValidationContext(dto),
            validateAllProperties: true);

        // 2. Normalize persisted data.
        var fullName = dto.FullName.Trim();
        var email = NormalizeEmail(dto.Email);

        // 3. Check for an existing email.
        var existingUser = await users.GetByAsync(
            x => x.Email == email,
            cancellationToken: cancellationToken);

        if (existingUser is not null)
        {
            throw new BusinessException(
                BusinessErrorType.Conflict,
                "EMAIL_ALREADY_REGISTERED");
        }

        // 4. Server-owned account properties.
        // Never accept Role or account state from the client.
        var user = new User
        {
            FullName = fullName,
            Email = email,
            Role = UserRole.Customer,
            IsActive = true,
            IsEmailVerified = false
        };

        // 5. Hash the password before persistence.
        user.PasswordHash = passwordHasher.HashPassword(
            user,
            dto.Password);

        // 6. Persist the User using the shared DbContext.
        await users.AddAsync(
            user,
            cancellationToken);

        await unitOfWork.SaveChangesAsync(
            cancellationToken);

        // 7. Return only safe account information.
        return new RegisterResponseDto(
            user.Id,
            user.FullName,
            user.Email,
            user.CreatedAt);
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static BusinessException InvalidCredentials()
        => new(
            BusinessErrorType.Unauthorized,
            "INVALID_CREDENTIALS");
}
