using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class VoucherUsageReadRepository(
    IReadOnlyQueryRepository<Order> orders) : IVoucherUsageReadRepository
{
    public Task<bool> HasOrderHistoryAsync(
        Guid voucherId,
        CancellationToken cancellationToken = default)
        => orders.Query().AnyAsync(
            x => x.VoucherId == voucherId, cancellationToken);

    public Task<long> CountNonCancelledOrdersAsync(
        Guid voucherId,
        Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var query = NonCancelledOrders(voucherId);
        if (userId.HasValue)
            query = query.Where(x => x.UserId == userId.Value);

        return query.LongCountAsync(cancellationToken);
    }

    public Task<bool> HasCustomerUsageAboveLimitAsync(
        Guid voucherId,
        int perUserLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perUserLimit);

        // Count on the database; do not materialize Order entities.
        return NonCancelledOrders(voucherId)
            .GroupBy(x => x.UserId)
            .Where(group => group.LongCount() > perUserLimit)
            .Select(group => group.Key)
            .AnyAsync(cancellationToken);
    }

    private IQueryable<Order> NonCancelledOrders(Guid voucherId)
        => orders.Query().Where(x =>
            x.VoucherId == voucherId && x.Status != OrderStatus.Cancelled);
}
