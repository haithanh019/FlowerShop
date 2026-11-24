using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class VoucherUsageWriteRepository(
    FlowerShopDbContext db) : IVoucherUsageWriteRepository
{
    public async Task<bool> TryIncrementAsync(
        Guid voucherId, int expectedUsedCount,
        CancellationToken cancellationToken = default)
    {
        if (voucherId == Guid.Empty)
            throw new ArgumentException("VOUCHER_ID_REQUIRED", nameof(voucherId));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedUsedCount);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("TRANSACTION_REQUIRED");
        cancellationToken.ThrowIfCancellationRequested();

        // No tracked Voucher is needed. SQL updates only the counter and timestamp.
        // Check against the counter read during locked pricing and enforce the global limit.
        var utc = DateTime.UtcNow;
        var now = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
        var affected = await db.Vouchers
            .Where(x => x.Id == voucherId && x.IsActive &&
                x.UsedCount == expectedUsedCount && x.UsedCount < int.MaxValue &&
                (!x.UsageLimit.HasValue || x.UsedCount < x.UsageLimit.Value))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.UsedCount, x => x.UsedCount + 1)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryDecrementAsync(
        Guid voucherId, int expectedUsedCount,
        CancellationToken cancellationToken = default)
    {
        if (voucherId == Guid.Empty)
            throw new ArgumentException("VOUCHER_ID_REQUIRED", nameof(voucherId));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedUsedCount);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("TRANSACTION_REQUIRED");
        cancellationToken.ThrowIfCancellationRequested();

        // Returning a previously consumed use must not depend on current availability.
        // Caller owns the voucher lock and verifies Order history before this write.
        var utc = DateTime.UtcNow;
        var now = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
        var affected = await db.Vouchers
            .Where(x => x.Id == voucherId &&
                x.UsedCount == expectedUsedCount && x.UsedCount > 0)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.UsedCount, x => x.UsedCount - 1)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        return affected == 1;
    }
}
