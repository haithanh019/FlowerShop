namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IProductImagePrimaryRepository
{
    Task<bool> SetPrimaryAsync(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken = default);
}
