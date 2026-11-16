using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class ProductStockRepository(
    FlowerShopDbContext db) : IProductStockRepository
{
    public async Task<bool> TryIncreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var now = DateTime.UtcNow;

        var affected = await db.Set<Product>()
            .Where(p => p.Id == productId &&
                        p.StockQuantity <= int.MaxValue - quantity)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    p => p.StockQuantity,
                    p => p.StockQuantity + quantity)
                .SetProperty(p => p.UpdatedAt, now),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> TryDecreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var now = DateTime.UtcNow;

        var affected = await db.Set<Product>()
            .Where(p => p.Id == productId &&
                        p.IsActive &&
                        p.StockQuantity >= quantity)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    p => p.StockQuantity,
                    p => p.StockQuantity - quantity)
                .SetProperty(p => p.UpdatedAt, now),
                cancellationToken);

        return affected == 1;
    }
}
