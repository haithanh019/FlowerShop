namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IVoucherUsageWriteRepository
{
    // Caller must hold the voucher application lock in the current transaction.
    // A successful increment must commit together with the corresponding order.
    Task<bool> TryIncrementAsync(
        Guid voucherId, int expectedUsedCount,
        CancellationToken cancellationToken = default);
}
