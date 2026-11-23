
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class OrderReadRepository(FlowerShopDbContext db)
    : IOrderReadRepository
{
    // Get paginated order history belonging to a user
    public async Task<(IReadOnlyList<Order> Orders, int TotalCount)>
        GetPageByUserIdAsync(
            Guid userId,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var query = db.Orders
            .AsNoTracking()
            .Where(x => x.UserId == userId);

        // Count orders before pagination
        var totalCount = await query.CountAsync(cancellationToken);

        // Retrieve newest orders first
        var orders = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip(checked((page - 1) * pageSize))
            .Take(pageSize)
            .Include(x => x.Items)
            .Include(x => x.Payments)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return (orders, totalCount);
    }

    // Get order details only if the order belongs to the user
    public Task<Order?> GetByIdAndUserIdAsync(
        Guid orderId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return db.Orders
            .AsNoTracking()
            .Where(x => x.Id == orderId &&
                        x.UserId == userId)
            .Include(x => x.Items)
            .Include(x => x.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);
    }
}
