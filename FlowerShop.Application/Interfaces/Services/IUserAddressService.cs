using FlowerShop.Application.DTOs.UserAddresses;

namespace FlowerShop.Application.Interfaces.Services;

public interface IUserAddressService
{
    Task<IReadOnlyList<UserAddressDto>> GetAllAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<UserAddressDto> GetByIdAsync(
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken = default);

    Task<UserAddressDto> CreateAsync(
        Guid userId,
        UserAddressUpsertDto dto,
        CancellationToken cancellationToken = default);

    Task<UserAddressDto> UpdateAsync(
        Guid userId,
        Guid addressId,
        UserAddressUpsertDto dto,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken = default);
}
