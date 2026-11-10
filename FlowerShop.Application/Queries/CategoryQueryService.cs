
using System.Linq.Expressions;
using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class CategoryQueryService : ICategoryQueryService
{
    private readonly IReadOnlyQueryRepository<Category> _queryRepository;
    private readonly IGenericRepository<Category> _categoryRepository;

    private static readonly Expression<Func<Category, CategoryODataDto>>
        Projection = category => new CategoryODataDto
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        };

    private static readonly Func<Category, CategoryODataDto>
        MapToDto = Projection.Compile();

    public CategoryQueryService(
        IReadOnlyQueryRepository<Category> queryRepository,
        IGenericRepository<Category> categoryRepository)
    {
        _queryRepository = queryRepository;
        _categoryRepository = categoryRepository;
    }

    public IQueryable<CategoryODataDto> QueryCategories()
    {
        return _queryRepository
            .Query()
            .Select(Projection);
    }

    public async Task<CategoryODataDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var category = await _categoryRepository.GetByIdAsync(
            id,
            trackChanges: false,
            cancellationToken: cancellationToken);

        return category is null
            ? null
            : MapToDto(category);
    }
}
