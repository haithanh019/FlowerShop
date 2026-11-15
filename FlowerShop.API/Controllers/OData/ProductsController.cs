using FlowerShop.API.OData;
using FlowerShop.Application.DTOs.Products;
using FlowerShop.Application.Interfaces.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace FlowerShop.API.Controllers.OData;

public sealed class ProductsController(
    IProductQueryService service) : ODataController
{
    [ReadOnlyODataQuery("Id,Name,Slug,Price,CreatedAt,CategoryId")]
    public IQueryable<ProductODataDto> Get()
        => service.QueryProducts();

    public async Task<ActionResult<ProductODataDto>> Get(
        [FromRoute] Guid key,
        CancellationToken cancellationToken)
    {
        var result = await service.GetByIdAsync(key, cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }
}
