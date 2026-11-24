using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.UserProfiles;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class UserProfileService(
    IGenericRepository<User> users) : IUserProfileService
{
    public async Task<UserProfileDto> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        var user = await users.GetByAsync(
            x => x.Id == userId &&
                 x.IsActive &&
                 x.Role == UserRole.Customer,
            trackChanges: false,
            cancellationToken: cancellationToken)
                ?? throw new BusinessException(
                    BusinessErrorType.NotFound,
                    "USER_NOT_FOUND");

        return ToDto(user);
    }

    private static void ValidateUserId(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");
    }

    private static UserProfileDto ToDto(User user)
    {
        return new UserProfileDto(
            UserId: user.Id,
            FullName: user.FullName,
            Email: user.Email,
            PhoneNumber: user.PhoneNumber,
            IsEmailVerified: user.IsEmailVerified,
            CreatedAtUtc: AsUtc(user.CreatedAt),
            UpdatedAtUtc: user.UpdatedAt is { } updated
                ? AsUtc(updated)
                : null);
    }

    // Persisted timestamps follow the UTC convention.
    private static DateTime AsUtc(DateTime timestamp)
    {
        return DateTime.SpecifyKind(
            timestamp,
            DateTimeKind.Utc);
    }
}
