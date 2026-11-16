using FlowerShop.Application.DTOs.ProductImages;

namespace FlowerShop.Application.Interfaces.Queries;

public interface IProductImageQueryService
{
    Task<IReadOnlyList<ProductImageDto>> GetPublicAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductImageDto>> GetAdminAsync(
        Guid productId,
        CancellationToken cancellationToken = default);
}
