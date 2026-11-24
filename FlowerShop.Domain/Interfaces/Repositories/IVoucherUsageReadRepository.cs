namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IVoucherUsageReadRepository
{
    Task<bool> HasOrderHistoryAsync(
        Guid voucherId,
        CancellationToken cancellationToken = default);

    Task<long> CountNonCancelledOrdersAsync(
        Guid voucherId,
        Guid? userId = null,
        CancellationToken cancellationToken = default);

    Task<bool> HasCustomerUsageAboveLimitAsync(
        Guid voucherId,
        int perUserLimit,
        CancellationToken cancellationToken = default);
}
