using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.ProductImages;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class ProductImageQueryService(
    IGenericRepository<Product> products,
    IGenericRepository<ProductImage> images)
    : IProductImageQueryService
{
    public async Task<IReadOnlyList<ProductImageDto>> GetPublicAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var product = await products.GetByAsync(
            x => x.Id == productId && x.IsActive,
            cancellationToken: cancellationToken);

        return product is null
            ? throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_NOT_FOUND")
            : await GetImagesAsync(productId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductImageDto>> GetAdminAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var product = await products.GetByIdAsync(
            productId,
            cancellationToken: cancellationToken);

        return product is null
            ? throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_NOT_FOUND")
            : await GetImagesAsync(productId, cancellationToken);
    }

    private async Task<IReadOnlyList<ProductImageDto>> GetImagesAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var productImages = await images.FindAsync(
            x => x.ProductId == productId,
            cancellationToken: cancellationToken);

        return [.. productImages
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => new ProductImageDto(
                x.Id,
                x.ProductId,
                x.ImageUrl,
                x.IsPrimary,
                x.SortOrder,
                x.CreatedAt))];
    }
}
