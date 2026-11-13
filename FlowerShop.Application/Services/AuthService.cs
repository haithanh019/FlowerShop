using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Identity;

namespace FlowerShop.Application.Services;

public sealed class AuthService(
    IGenericRepository<User> users,
    IPasswordHasher<User> passwordHasher,
    IJwtTokenService jwtTokenService) : IAuthService
{
    public async Task<LoginResponseDto> LoginAsync(
        LoginDto dto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto, new ValidationContext(dto), true);

        var email = dto.Email.Trim();

        var user = await users.GetByAsync(
            x => x.Email == email,
            cancellationToken: cancellationToken);

        if (user is null || !user.IsActive)
            throw InvalidCredentials();

        var result = passwordHasher.VerifyHashedPassword(
            user, user.PasswordHash, dto.Password);

        if (result == PasswordVerificationResult.Failed)
            throw InvalidCredentials();

        return jwtTokenService.CreateToken(user);
    }

    private static BusinessException InvalidCredentials()
        => new(BusinessErrorType.Unauthorized, "INVALID_CREDENTIALS");
}
