using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Mappings;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class CategoryQueryService(
    IReadOnlyQueryRepository<Category> queryRepository,
    IGenericRepository<Category> categoryRepository)
    : ICategoryQueryService
{
    public IQueryable<CategoryODataDto> QueryCategories()
        => queryRepository.Query()
            .Select(CategoryMapper.Projection);

    public async Task<CategoryODataDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var category = await categoryRepository.GetByIdAsync(
            id,
            cancellationToken: cancellationToken);

        return category?.ToDto();
    }
}
