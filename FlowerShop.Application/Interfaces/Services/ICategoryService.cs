
using FlowerShop.Application.DTOs.Categories;

namespace FlowerShop.Application.Interfaces.Services;

public interface ICategoryService
{
    Task<CategoryODataDto> CreateAsync(
        CreateCategoryDto dto,
        CancellationToken cancellationToken = default);

    Task<CategoryODataDto> UpdateAsync(
        Guid id,
        UpdateCategoryDto dto,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
