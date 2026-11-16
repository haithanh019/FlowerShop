using FlowerShop.Application.DTOs.Products;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/products/{id:guid}/stock")]
[Authorize(Roles = "Admin")]
public sealed class ProductStockController(
    IProductStockService service) : ControllerBase
{
    [HttpPost("increase")]
    public async Task<IActionResult> Increase(
        Guid id,
        [FromBody] StockChangeDto dto,
        CancellationToken cancellationToken)
    {
        await service.IncreaseAsync(id, dto.Quantity, cancellationToken);
        return NoContent();
    }

    [HttpPost("decrease")]
    public async Task<IActionResult> Decrease(
        Guid id,
        [FromBody] StockChangeDto dto,
        CancellationToken cancellationToken)
    {
        await service.DecreaseAsync(id, dto.Quantity, cancellationToken);
        return NoContent();
    }
}
