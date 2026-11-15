using System.Linq.Expressions;
using FlowerShop.Application.DTOs.Products;
using FlowerShop.Domain.Entities;

namespace FlowerShop.Application.Mappings;

public static class ProductMapper
{
    public static readonly Expression<Func<Product, ProductODataDto>>
        Projection = product => new ProductODataDto
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            Description = product.Description,
            Price = product.Price,
            CategoryId = product.CategoryId,
            IsInStock = product.StockQuantity > 0,
            CreatedAt = product.CreatedAt
        };

    private static readonly Func<Product, ProductODataDto>
        MapToDto = Projection.Compile();

    public static ProductODataDto ToDto(this Product product)
        => MapToDto(product);

    public static AdminProductODataDto ToAdminDto(this Product product)
        => new()
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            Description = product.Description,
            Price = product.Price,
            StockQuantity = product.StockQuantity,
            IsActive = product.IsActive,
            CategoryId = product.CategoryId,
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt
        };

    public static Product ToEntity(this CreateProductDto dto)
    {
        var product = new Product
        {
            StockQuantity = dto.StockQuantity,
            IsActive = dto.IsActive
        };

        Apply(dto, product);
        return product;
    }

    public static void ApplyTo(
        this UpdateProductDto dto,
        Product product)
    {
        Apply(dto, product);
        product.IsActive = dto.IsActive;
    }

    private static void Apply(ProductUpsertDto dto, Product product)
    {
        product.Name = dto.Name.Trim();
        product.Slug = dto.Slug.Trim();
        product.Description = string.IsNullOrWhiteSpace(dto.Description)
            ? null
            : dto.Description.Trim();

        product.Price = dto.Price;
        product.CategoryId = dto.CategoryId;
    }
}
