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
}
