
using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Domain.Entities;

namespace FlowerShop.Application.Mappings;

public static class CategoryMapper
{
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
