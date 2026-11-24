using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.UserProfiles;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class UserProfileService(
    IGenericRepository<User> users,
    IUnitOfWork unitOfWork) : IUserProfileService
{
    public async Task<UserProfileDto> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        var user = await GetActiveCustomerAsync(
            userId,
            trackChanges: false,
            cancellationToken: cancellationToken);

        return ToDto(user);
    }

    public async Task<UserProfileDto> UpdateAsync(
        Guid userId,
        UpdateUserProfileDto dto,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate input.
        ValidateUserId(userId);
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto,
            new ValidationContext(dto),
            validateAllProperties: true);

        // 2. Normalize editable fields.
        var fullName = dto.FullName.Trim();

        var phoneNumber = string.IsNullOrWhiteSpace(dto.PhoneNumber)
            ? null
            : dto.PhoneNumber.Trim();

        // 3. Serialize profile updates for this Customer.
        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:user-profile:{userId:N}",
            async ct =>
            {
                var user = await GetActiveCustomerAsync(
                    userId,
                    trackChanges: true,
                    cancellationToken: ct);

                // 4. A normalized no-op must preserve
                // the existing UpdatedAt timestamp.
                if (user.FullName == fullName &&
                    user.PhoneNumber == phoneNumber)
                {
                    return ToDto(user);
                }

                // 5. Modify only permitted fields.
                user.FullName = fullName;
                user.PhoneNumber = phoneNumber;

                // 6. Match SQL Server datetime2(3).
                var now = DateTime.UtcNow;

                user.UpdatedAt = now.AddTicks(
                    -(now.Ticks % TimeSpan.TicksPerMillisecond));

                // EF Core tracks the modified fields.
                // UnitOfWork saves and commits automatically.
                return ToDto(user);
            },
            cancellationToken);
    }

    private async Task<User> GetActiveCustomerAsync(
        Guid userId,
        bool trackChanges,
        CancellationToken cancellationToken)
    {
        return await users.GetByAsync(
            x => x.Id == userId &&
                 x.IsActive &&
                 x.Role == UserRole.Customer,
            trackChanges: trackChanges,
            cancellationToken: cancellationToken)
                ?? throw new BusinessException(
                    BusinessErrorType.NotFound,
                    "USER_NOT_FOUND");
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

    private static DateTime AsUtc(DateTime timestamp)
    {
        return DateTime.SpecifyKind(
            timestamp,
            DateTimeKind.Utc);
    }
}
