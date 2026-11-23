using FlowerShop.Domain.Entities;

namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IOrderReadRepository
{
    Task<(IReadOnlyList<Order> Orders, int TotalCount)>
        GetPageByUserIdAsync(
            Guid userId,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default);

    Task<Order?> GetByIdAndUserIdAsync(
        Guid orderId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
