using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Mappings;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;

namespace FlowerShop.Application.Services;

public sealed class CategoryService : ICategoryService
{
    private readonly IUnitOfWork _unitOfWork;

    public CategoryService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<CategoryODataDto> CreateAsync(
        CreateCategoryDto dto,
        CancellationToken cancellationToken = default)
    {
        Validate(dto);

        var repository = _unitOfWork.GetRepository<Category>();

        await EnsureSlugAvailableAsync(
            dto.Slug.Trim(), null, cancellationToken);

        var category = dto.ToEntity();

        await repository.AddAsync(category, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return category.ToDto();
    }

    public async Task<CategoryODataDto> UpdateAsync(
        Guid id,
        UpdateCategoryDto dto,
        CancellationToken cancellationToken = default)
    {
        Validate(dto);

        var repository = _unitOfWork.GetRepository<Category>();

        var category = await repository.GetByIdAsync(
            id, trackChanges: true, cancellationToken: cancellationToken);

        if (category is null)
            throw new BusinessException(
                BusinessErrorType.NotFound, "CATEGORY_NOT_FOUND");

        await EnsureSlugAvailableAsync(
            dto.Slug.Trim(), id, cancellationToken);

        dto.ApplyTo(category);
        category.UpdatedAt = DateTime.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return category.ToDto();
    }

    public async Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var repository = _unitOfWork.GetRepository<Category>();

        var category = await repository.GetByIdAsync(
            id, trackChanges: true, cancellationToken: cancellationToken);

        if (category is null)
            throw new BusinessException(
                BusinessErrorType.NotFound, "CATEGORY_NOT_FOUND");

        var product = await _unitOfWork
            .GetRepository<Product>()
            .GetByAsync(
                x => x.CategoryId == id,
                cancellationToken: cancellationToken);

        if (product is not null)
            throw new BusinessException(
                BusinessErrorType.Conflict, "CATEGORY_HAS_PRODUCTS");

        repository.Delete(category);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSlugAvailableAsync(
        string slug,
        Guid? excludedId,
        CancellationToken cancellationToken)
    {
        var exists = await _unitOfWork
            .GetRepository<Category>()
            .GetByAsync(
                x => x.Slug == slug &&
                     (!excludedId.HasValue || x.Id != excludedId.Value),
                cancellationToken: cancellationToken);

        if (exists is not null)
            throw new BusinessException(
                BusinessErrorType.Conflict, "CATEGORY_SLUG_EXISTS");
    }

    private static void Validate(CategoryUpsertDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto,
            new ValidationContext(dto),
            validateAllProperties: true);
    }
}
