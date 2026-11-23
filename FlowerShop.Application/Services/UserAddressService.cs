using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.UserAddresses;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class UserAddressService(
    IGenericRepository<UserAddress> addresses,
    IGenericRepository<User> users,
    IUnitOfWork unitOfWork)
    : IUserAddressService
{
    public async Task<IReadOnlyList<UserAddressDto>> GetAllAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        var userAddresses = await addresses.FindAsync(
            x => x.UserId == userId,
            cancellationToken: cancellationToken);

        return
        [
            .. userAddresses
                .OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Select(ToDto)
        ];
    }

    public async Task<UserAddressDto> GetByIdAsync(
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        if (addressId == Guid.Empty)
            throw new ValidationException("INVALID_ADDRESS_ID");

        var address = await addresses.GetByAsync(
            x => x.Id == addressId &&
                 x.UserId == userId,
            cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound,
                "ADDRESS_NOT_FOUND");
        return ToDto(address);
    }

    public async Task<UserAddressDto> CreateAsync(
        Guid userId,
        UserAddressUpsertDto dto,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate input before opening a transaction.
        ValidateUserId(userId);
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto,
            new ValidationContext(dto),
            validateAllProperties: true);

        // 2. Serialize address mutations for this Customer.
        // The UnitOfWork commits after the callback succeeds.
        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:user-addresses:{userId:N}",
            async ct =>
            {
                // 3. Verify that the Customer is active.
                var user = await users.GetByAsync(
                    x => x.Id == userId &&
                         x.IsActive &&
                         x.Role == UserRole.Customer,
                    cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.NotFound,
                        "USER_NOT_FOUND");

                // 4. Check whether this Customer already
                // has a default address.
                var existingDefault = await addresses.GetByAsync(
                    x => x.UserId == userId &&
                         x.IsDefault,
                    cancellationToken: ct);

                // 5. Never accept UserId or IsDefault
                // from the client.
                var address = new UserAddress
                {
                    UserId = userId,

                    RecipientName = dto.RecipientName.Trim(),
                    RecipientPhone = dto.RecipientPhone.Trim(),

                    AddressLine = dto.AddressLine.Trim(),
                    Ward = dto.Ward.Trim(),
                    District = dto.District.Trim(),
                    Province = dto.Province.Trim(),

                    CountryCode = dto.CountryCode
                        .Trim()
                        .ToUpperInvariant(),

                    PostalCode = string.IsNullOrWhiteSpace(
                        dto.PostalCode)
                        ? null
                        : dto.PostalCode.Trim(),

                    // First address becomes default.
                    // Also repairs the no-default case
                    // when adding a new address.
                    IsDefault = existingDefault is null
                };

                // 6. Add to the shared DbContext.
                await addresses.AddAsync(address, ct);

                // ExecuteInLockedTransactionAsync calls
                // SaveChangesAsync and commits the transaction.
                // Do not call SaveChangesAsync here.

                return ToDto(address);
            },
            cancellationToken);
    }

    private static void ValidateUserId(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");
    }

    private static UserAddressDto ToDto(UserAddress address)
    {
        return new UserAddressDto(
            AddressId: address.Id,
            RecipientName: address.RecipientName,
            RecipientPhone: address.RecipientPhone,
            AddressLine: address.AddressLine,
            Ward: address.Ward,
            District: address.District,
            Province: address.Province,
            CountryCode: address.CountryCode,
            PostalCode: address.PostalCode,
            IsDefault: address.IsDefault,
            CreatedAtUtc: AsUtc(address.CreatedAt),
            UpdatedAtUtc: address.UpdatedAt is { } updated
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
