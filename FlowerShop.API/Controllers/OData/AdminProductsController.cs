using FlowerShop.API.OData;
using FlowerShop.Application.DTOs.Products;
using FlowerShop.Application.Interfaces.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace FlowerShop.API.Controllers.OData;

[Authorize(Roles = "Admin")]
public sealed class AdminProductsController(
    IProductQueryService service) : ODataController
{
    [ReadOnlyODataQuery(
        "Id,Name,Slug,Price,StockQuantity,IsActive,CategoryId,CreatedAt,UpdatedAt")]
    public IQueryable<AdminProductODataDto> Get()
        => service.QueryAdminProducts();

    public async Task<ActionResult<AdminProductODataDto>> Get(
        [FromRoute] Guid key,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAdminByIdAsync(
            key, cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }
}
