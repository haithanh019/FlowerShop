using FlowerShop.Application.DTOs.Categories;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace FlowerShop.API.OData;

public static class EdmModelFactory
{
    public static IEdmModel Create()
    {
        var builder = new ODataConventionModelBuilder();

        var categories =
            builder.EntitySet<CategoryODataDto>("Categories");

        categories.EntityType.HasKey(x => x.Id);

        return builder.GetEdmModel();
    }
}