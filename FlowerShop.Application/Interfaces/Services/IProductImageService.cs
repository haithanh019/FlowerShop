using FlowerShop.Application.DTOs.ProductImages;

namespace FlowerShop.Application.Interfaces.Services;

public interface IProductImageService
{
    Task<ProductImageDto> UploadAsync(
        Guid productId,
        Stream stream,
        string fileName,
        string contentType,
        long fileLength,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken = default);

    Task SetPrimaryAsync(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken = default);

    Task ReorderAsync(
        Guid productId,
        IReadOnlyList<Guid> orderedImageIds,
        CancellationToken cancellationToken = default);

}
