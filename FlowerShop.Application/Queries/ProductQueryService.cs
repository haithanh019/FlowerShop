using FlowerShop.Application.DTOs.Products;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Mappings;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class ProductQueryService(
    IReadOnlyQueryRepository<Product> queryRepository,
    IGenericRepository<Product> productRepository)
    : IProductQueryService
{
    public IQueryable<ProductODataDto> QueryProducts()
        => queryRepository.Query()
            .Where(x => x.IsActive)
            .Select(ProductMapper.Projection);

    public async Task<ProductODataDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var product = await productRepository.GetByAsync(
            x => x.Id == id && x.IsActive,
            cancellationToken: cancellationToken);

        return product?.ToDto();
    }
    public IQueryable<AdminProductODataDto> QueryAdminProducts()
        => queryRepository.Query()
            .Select(ProductMapper.AdminProjection);

    public async Task<AdminProductODataDto?> GetAdminByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var product = await productRepository.GetByIdAsync(
            id,
            cancellationToken: cancellationToken);

        return product?.ToAdminDto();
    }

}
