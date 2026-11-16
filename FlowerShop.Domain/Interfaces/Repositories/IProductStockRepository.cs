namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IProductStockRepository
{
    Task<bool> TryIncreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default);

    Task<bool> TryDecreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default);
}
