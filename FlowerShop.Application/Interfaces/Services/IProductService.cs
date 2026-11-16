using FlowerShop.Application.DTOs.Products;

namespace FlowerShop.Application.Interfaces.Services;

public interface IProductService
{
    Task<AdminProductODataDto> CreateAsync(
        CreateProductDto dto,
        CancellationToken cancellationToken = default);

    Task<AdminProductODataDto> UpdateAsync(
        Guid id,
        UpdateProductDto dto,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
