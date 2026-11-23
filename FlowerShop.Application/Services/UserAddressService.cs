using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.UserAddresses;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class UserAddressService(
    IGenericRepository<UserAddress> addresses)
    : IUserAddressService
{
    public async Task<IReadOnlyList<UserAddressDto>> GetAllAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        // 1. Only retrieve addresses belonging to this user.
        // GenericRepository uses AsNoTracking by default.
        var userAddresses = await addresses.FindAsync(
            x => x.UserId == userId,
            cancellationToken: cancellationToken);

        // 2. Default address first.
        // Other addresses are ordered by creation time.
        return [.. userAddresses
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(ToDto)];
    }

    public async Task<UserAddressDto> GetByIdAsync(
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        if (addressId == Guid.Empty)
            throw new ValidationException("INVALID_ADDRESS_ID");

        // Both AddressId and UserId must match.
        // Never retrieve by AddressId alone.
        var address = await addresses.GetByAsync(
            x => x.Id == addressId &&
                 x.UserId == userId,
            cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound,
                "ADDRESS_NOT_FOUND");
        return ToDto(address);
    }

    private static void ValidateUserId(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");
    }

    private static UserAddressDto ToDto(
        UserAddress address)
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

    // SQL Server datetime2 does not retain DateTime.Kind.
    // Application convention: persisted timestamps are UTC.
    private static DateTime AsUtc(DateTime timestamp)
    {
        return DateTime.SpecifyKind(
            timestamp,
            DateTimeKind.Utc);
    }
}
