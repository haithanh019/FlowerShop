
using FlowerShop.Application.DTOs.Categories;

namespace FlowerShop.Application.Interfaces.Queries;

public interface ICategoryQueryService
{
    IQueryable<CategoryODataDto> QueryCategories();

    Task<CategoryODataDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
