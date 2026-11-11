using System.Linq.Expressions;
using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Domain.Entities;

namespace FlowerShop.Application.Mappings;

public static class CategoryMapper
{
    public static readonly Expression<Func<Category, CategoryODataDto>>
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

    public static CategoryODataDto ToDto(this Category category)
        => MapToDto(category);

    public static Category ToEntity(this CreateCategoryDto dto)
    {
        var category = new Category();
        Apply(dto, category);

        return category;
    }

    public static void ApplyTo(
        this UpdateCategoryDto dto,
        Category category)
    {
        Apply(dto, category);
    }

    private static void Apply(
        CategoryUpsertDto dto,
        Category category)
    {
        category.Name = dto.Name.Trim();
        category.Slug = dto.Slug.Trim();

        category.Description =
            string.IsNullOrWhiteSpace(dto.Description)
                ? null
                : dto.Description.Trim();
    }
}
