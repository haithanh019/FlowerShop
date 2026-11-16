using System.Data;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class ProductImagePrimaryRepository(
    FlowerShopDbContext db) : IProductImagePrimaryRepository
{
    public async Task<bool> SetPrimaryAsync(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var transaction =
                await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);

            var now = DateTime.UtcNow;

            // Remove the previous primary image
            await db.ProductImages
                .Where(x => x.ProductId == productId && x.IsPrimary)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.IsPrimary, false)
                    .SetProperty(x => x.UpdatedAt, now),
                    cancellationToken);

            // Assign the new primary image
            var affected = await db.ProductImages
                .Where(x => x.ProductId == productId && x.Id == imageId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.IsPrimary, true)
                    .SetProperty(x => x.UpdatedAt, now),
                    cancellationToken);

            if (affected != 1)
                return false;

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (SqlException ex)
            when (ex.Number is 2601 or 2627 or 1205)
        {
            return false;
        }
    }
}
