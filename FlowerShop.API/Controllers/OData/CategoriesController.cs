using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Application.Interfaces.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace FlowerShop.API.Controllers.OData;

public sealed class CategoriesController : ODataController
{
    private readonly ICategoryQueryService _categoryQueryService;

    public CategoriesController(
        ICategoryQueryService categoryQueryService)
    {
        _categoryQueryService = categoryQueryService;
    }

    // GET /odata/v1/Categories
    [EnableQuery(
        PageSize = 20,
        MaxTop = 100,
        MaxSkip = 10000,
        MaxNodeCount = 30,
        MaxOrderByNodeCount = 2,
        EnsureStableOrdering = true,
        AllowedOrderByProperties = "Id,Name,Slug,CreatedAt",
        AllowedQueryOptions =
            AllowedQueryOptions.Filter |
            AllowedQueryOptions.OrderBy |
            AllowedQueryOptions.Select |
            AllowedQueryOptions.Top |
            AllowedQueryOptions.Skip |
            AllowedQueryOptions.Count)]
    public IQueryable<CategoryODataDto> Get()
    {
        return _categoryQueryService.QueryCategories();
    }

    // GET /odata/v1/Categories({key})
    public async Task<ActionResult<CategoryODataDto>> Get(
        [FromRoute] Guid key,
        CancellationToken cancellationToken)
    {
        var category = await _categoryQueryService.GetByIdAsync(
            key,
            cancellationToken);

        if (category is null)
        {
            return NotFound();
        }

        return Ok(category);
    }
}
