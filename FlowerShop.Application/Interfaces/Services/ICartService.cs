using FlowerShop.Application.DTOs.Carts;

namespace FlowerShop.Application.Interfaces.Services;

public interface ICartService
{
    Task AddItemAsync(
        Guid userId,
        AddCartItemDto dto,
        CancellationToken cancellationToken = default);

    Task UpdateQuantityAsync(
        Guid userId,
        Guid cartItemId,
        UpdateCartItemDto dto,
        CancellationToken cancellationToken = default);

    Task RemoveItemAsync(
        Guid userId,
        Guid cartItemId,
        CancellationToken cancellationToken = default);
}
