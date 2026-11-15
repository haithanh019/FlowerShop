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
}
