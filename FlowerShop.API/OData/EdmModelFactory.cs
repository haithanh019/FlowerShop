using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Application.DTOs.Products;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace FlowerShop.API.OData;

public static class EdmModelFactory
{
    public static IEdmModel Create()
    {
        var builder = new ODataConventionModelBuilder();

        // Category Entity Set
        var categories =
            builder.EntitySet<CategoryODataDto>("Categories");

        categories.EntityType.HasKey(x => x.Id);

        // Product Entity Set
        var products =
            builder.EntitySet<ProductODataDto>("Products");

        products.EntityType.HasKey(x => x.Id);

        return builder.GetEdmModel();
    }
}