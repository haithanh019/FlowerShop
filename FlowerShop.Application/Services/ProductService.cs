using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Products;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Mappings;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;

namespace FlowerShop.Application.Services;

public sealed class ProductService(IUnitOfWork unitOfWork)
    : IProductService
{
    public async Task<AdminProductODataDto> CreateAsync(
        CreateProductDto dto,
        CancellationToken cancellationToken = default)
    {
        Validate(dto);

        await EnsureCategoryExistsAsync(
            dto.CategoryId, cancellationToken);

        await EnsureSlugAvailableAsync(
            dto.Slug, null, cancellationToken);

        var product = dto.ToEntity();

        await unitOfWork.GetRepository<Product>()
            .AddAsync(product, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return product.ToAdminDto();
    }

    public async Task<AdminProductODataDto> UpdateAsync(
        Guid id,
        UpdateProductDto dto,
        CancellationToken cancellationToken = default)
    {
        Validate(dto);

        var product = await unitOfWork.GetRepository<Product>()
            .GetByIdAsync(
                id,
                trackChanges: true,
                cancellationToken: cancellationToken);

        if (product is null)
            throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_NOT_FOUND");

        await EnsureCategoryExistsAsync(
            dto.CategoryId, cancellationToken);

        await EnsureSlugAvailableAsync(
            dto.Slug, id, cancellationToken);

        dto.ApplyTo(product);
        product.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return product.ToAdminDto();
    }

    public async Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var product = await unitOfWork.GetRepository<Product>()
            .GetByIdAsync(
                id,
                trackChanges: true,
                cancellationToken: cancellationToken);

        if (product is null)
            throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_NOT_FOUND");

        if (!product.IsActive)
            return;

        product.IsActive = false;
        product.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureCategoryExistsAsync(
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var category = await unitOfWork.GetRepository<Category>()
            .GetByIdAsync(categoryId, cancellationToken: cancellationToken);

        if (category is null)
            throw new BusinessException(
                BusinessErrorType.NotFound, "CATEGORY_NOT_FOUND");
    }

    private async Task EnsureSlugAvailableAsync(
        string slug,
        Guid? excludedId,
        CancellationToken cancellationToken)
    {
        var exists = await unitOfWork.GetRepository<Product>()
            .GetByAsync(
                x => x.Slug == slug &&
                    (!excludedId.HasValue || x.Id != excludedId.Value),
                cancellationToken: cancellationToken);

        if (exists is not null)
            throw new BusinessException(
                BusinessErrorType.Conflict, "PRODUCT_SLUG_EXISTS");
    }

    private static void Validate(ProductUpsertDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto, new ValidationContext(dto), true);

        if (dto.CategoryId == Guid.Empty)
            throw new ValidationException("CATEGORY_ID_REQUIRED");

        if (dto.Price != decimal.Round(dto.Price, 2))
            throw new ValidationException("PRICE_MAX_TWO_DECIMALS");
    }
}
