using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class AdminOrderReadRepository(
    FlowerShopDbContext db) : IAdminOrderReadRepository
{
    public async Task<(
        IReadOnlyList<Order> Orders,
        int TotalCount)> GetPageAsync(
            OrderStatus? status,
            Guid? customerUserId,
            string? orderCodeKeyword,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
    {
        // 1. Validate query parameters.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        if (pageSize > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                "PAGE_SIZE_LIMIT_EXCEEDED");
        }

        if (status.HasValue && !Enum.IsDefined(status.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "INVALID_ORDER_STATUS");
        }

        if (customerUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "CUSTOMER_USER_ID_REQUIRED",
                nameof(customerUserId));
        }

        // Prevent overflow when calculating Skip().
        var offset = checked((page - 1) * pageSize);

        // 2. Base read-only query.
        IQueryable<Order> query = db.Orders
            .AsNoTracking();

        // 3. Optional filtering.
        if (status.HasValue)
        {
            query = query.Where(
                x => x.Status == status.Value);
        }

        if (customerUserId.HasValue)
        {
            query = query.Where(
                x => x.UserId == customerUserId.Value);
        }

        var keyword = orderCodeKeyword?.Trim();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(
                x => x.OrderCode.Contains(keyword));
        }

        // 4. Count BEFORE applying pagination.
        var totalCount = await query.CountAsync(
            cancellationToken);

        // 5. Stable sorting and pagination.
        var orders = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip(offset)
            .Take(pageSize)

            // Admin needs customer identity.
            .Include(x => x.User)

            // Required for OrderSummary projections.
            .Include(x => x.Items)
            .Include(x => x.Payments)

            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return (orders, totalCount);
    }

    public Task<Order?> GetByIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException(
                "ORDER_ID_REQUIRED",
                nameof(orderId));
        }

        // Admin can retrieve any order.
        // Authorization belongs to Application/API layers.
        return db.Orders
            .AsNoTracking()
            .Where(x => x.Id == orderId)
            .Include(x => x.User)
            .Include(x => x.Items)
            .Include(x => x.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);
    }
}
