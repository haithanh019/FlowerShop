using FlowerShop.Application.DTOs.Carts;

namespace FlowerShop.Application.Interfaces.Queries;

public interface ICartQueryService
{
    Task<CartDto> GetByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
