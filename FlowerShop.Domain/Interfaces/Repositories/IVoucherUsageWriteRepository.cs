namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IVoucherUsageWriteRepository
{
    // Caller must hold the voucher application lock in the current transaction.
    // A successful increment must commit together with the corresponding order.
    Task<bool> TryIncrementAsync(
        Guid voucherId, int expectedUsedCount,
        CancellationToken cancellationToken = default);

    // Caller must hold the same voucher lock used for redemption and administration.
    // Decrement and the Order cancellation must commit together.
    Task<bool> TryDecrementAsync(
        Guid voucherId, int expectedUsedCount,
        CancellationToken cancellationToken = default);
}
