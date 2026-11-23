
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
        ValidateAddressId(addressId);

        var address = await addresses.GetByAsync(
            x => x.Id == addressId &&
                 x.UserId == userId,
            cancellationToken: cancellationToken) ?? throw AddressNotFound();
        return ToDto(address);
    }

    public async Task<UserAddressDto> CreateAsync(
        Guid userId,
        UserAddressUpsertDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);
        ValidateDto(dto);

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            AddressLockKey(userId),
            async ct =>
            {
                await EnsureActiveCustomerAsync(userId, ct);

                var existingDefault = await addresses.GetByAsync(
                    x => x.UserId == userId && x.IsDefault,
                    cancellationToken: ct);

                var address = new UserAddress
                {
                    UserId = userId,
                    IsDefault = existingDefault is null
                };

                ApplyFields(address, dto);

                await addresses.AddAsync(address, ct);

                // UnitOfWork saves and commits after callback.
                return ToDto(address);
            },
            cancellationToken);
    }

    public async Task<UserAddressDto> UpdateAsync(
        Guid userId,
        Guid addressId,
        UserAddressUpsertDto dto,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate identifiers and request body.
        ValidateUserId(userId);
        ValidateAddressId(addressId);
        ValidateDto(dto);

        // 2. Use the same lock as CreateAsync.
        return await unitOfWork.ExecuteInLockedTransactionAsync(
            AddressLockKey(userId),
            async ct =>
            {
                // 3. Check account state inside transaction.
                await EnsureActiveCustomerAsync(userId, ct);

                // 4. Ownership must be part of the query.
                // trackChanges: true is required for EF updates.
                var address = await addresses.GetByAsync(
                    x => x.Id == addressId &&
                         x.UserId == userId,
                    trackChanges: true,
                    cancellationToken: ct) ?? throw AddressNotFound();

                // 5. Update only editable address fields.
                ApplyFields(address, dto);

                // Never change:
                // Id, UserId, CreatedAt, IsDefault.
                address.UpdatedAt = DateTime.UtcNow;

                // The entity is already tracked.
                // Do not call addresses.Update().
                // Do not call SaveChangesAsync().
                return ToDto(address);
            },
            cancellationToken);
    }


    public async Task DeleteAsync(
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate input before opening a transaction.
        ValidateUserId(userId);
        ValidateAddressId(addressId);

        // 2. Serialize mutations for the same Customer.
        _ = await unitOfWork.ExecuteInLockedTransactionAsync(
            AddressLockKey(userId),
            async ct =>
            {
                // 3. Only active Customers can delete addresses.
                await EnsureActiveCustomerAsync(userId, ct);

                // 4. Enforce ownership in the database query.
                var address = await addresses.GetByAsync(
                    x => x.Id == addressId &&
                         x.UserId == userId,
                    trackChanges: true,
                    cancellationToken: ct) ?? throw AddressNotFound();

                UserAddress? replacement = null;

                // 5. A replacement is needed only when
                // the deleted address is the default.
                if (address.IsDefault)
                {
                    var remaining = await addresses.FindAsync(
                        x => x.UserId == userId &&
                             x.Id != addressId,
                        trackChanges: true,
                        cancellationToken: ct);

                    replacement = remaining
                        .OrderBy(x => x.CreatedAt)
                        .ThenBy(x => x.Id)
                        .FirstOrDefault();
                }

                // 6. Mark the requested address for deletion.
                addresses.Delete(address);

                if (replacement is not null)
                {
                    // IMPORTANT:
                    // Flush the DELETE before promoting another
                    // address, to avoid violating the filtered
                    // unique index UX_UserAddresses_OneDefault.
                    //
                    // This SaveChanges remains inside the
                    // current SQL transaction. It does not commit.
                    await unitOfWork.SaveChangesAsync(ct);

                    // 7. Promote the selected replacement.
                    // It is already tracked by EF Core.
                    replacement.IsDefault = true;
                    replacement.UpdatedAt = DateTime.UtcNow;
                }

                // The UnitOfWork performs the final SaveChanges
                // and commits only after this callback succeeds.
                return true;
            },
            cancellationToken);
    }

    private async Task EnsureActiveCustomerAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByAsync(
            x => x.Id == userId &&
                 x.IsActive &&
                 x.Role == UserRole.Customer,
            cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound,
                "USER_NOT_FOUND");
    }

    private static void ApplyFields(
        UserAddress address,
        UserAddressUpsertDto dto)
    {
        address.RecipientName = dto.RecipientName.Trim();
        address.RecipientPhone = dto.RecipientPhone.Trim();

        address.AddressLine = dto.AddressLine.Trim();
        address.Ward = dto.Ward.Trim();
        address.District = dto.District.Trim();
        address.Province = dto.Province.Trim();

        address.CountryCode = dto.CountryCode
            .Trim()
            .ToUpperInvariant();

        address.PostalCode =
            string.IsNullOrWhiteSpace(dto.PostalCode)
                ? null
                : dto.PostalCode.Trim();
    }

    private static string AddressLockKey(Guid userId)
    {
        return $"flower-shop:user-addresses:{userId:N}";
    }

    private static void ValidateDto(
        UserAddressUpsertDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto,
            new ValidationContext(dto),
            validateAllProperties: true);
    }

    private static void ValidateUserId(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ValidationException(
                "INVALID_USER_ID");
        }
    }

    private static void ValidateAddressId(Guid addressId)
    {
        if (addressId == Guid.Empty)
        {
            throw new ValidationException(
                "INVALID_ADDRESS_ID");
        }
    }

    private static BusinessException AddressNotFound()
    {
        return new BusinessException(
            BusinessErrorType.NotFound,
            "ADDRESS_NOT_FOUND");
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
