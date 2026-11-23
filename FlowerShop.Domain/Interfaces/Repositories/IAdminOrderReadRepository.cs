using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IAdminOrderReadRepository
{
    Task<(IReadOnlyList<Order> Orders, int TotalCount)>
        GetPageAsync(
            OrderStatus? status,
            Guid? customerUserId,
            string? orderCodeKeyword,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default);

    Task<Order?> GetByIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);
}
