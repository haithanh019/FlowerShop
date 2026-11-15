using FlowerShop.Application.DTOs.Products;

namespace FlowerShop.Application.Interfaces.Queries;

public interface IProductQueryService
{
    IQueryable<ProductODataDto> QueryProducts();

    Task<ProductODataDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
