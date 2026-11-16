namespace FlowerShop.Application.Interfaces.Services;

public interface IProductStockService
{
    Task IncreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default);

    Task DecreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default);
}
