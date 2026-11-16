using FlowerShop.API.OData;
using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Application.Interfaces.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace FlowerShop.API.Controllers.OData;

public sealed class CategoriesController(
    ICategoryQueryService service) : ODataController
{
    [ReadOnlyODataQuery("Id,Name,Slug,CreatedAt")]
    public IQueryable<CategoryODataDto> Get()
        => service.QueryCategories();

    public async Task<ActionResult<CategoryODataDto>> Get(
        [FromRoute] Guid key,
        CancellationToken cancellationToken)
    {
        var result = await service.GetByIdAsync(key, cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }
}
