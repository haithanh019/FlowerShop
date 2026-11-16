using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize(Roles = "Customer")]
public sealed class OrdersController(
    IOrderQueryService orderQueryService) : ControllerBase
{
    // GET /api/orders?page=1&pageSize=10
    [HttpGet]
    [ProducesResponseType(
        typeof(OrderHistoryDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(
        StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await orderQueryService.GetHistoryAsync(
            userId,
            page,
            pageSize,
            cancellationToken);

        return Ok(result);
    }

    // GET /api/orders/{orderId}
    [HttpGet("{orderId:guid}")]
    [ProducesResponseType(
        typeof(OrderDetailDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(
        StatusCodes.Status403Forbidden)]
    [ProducesResponseType(
        StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await orderQueryService.GetDetailAsync(
            userId,
            orderId,
            cancellationToken);

        return Ok(result);
    }

    private bool TryGetUserId(out Guid userId)
    {
        return Guid.TryParse(
            User.FindFirst("sub")?.Value,
            out userId)
            && userId != Guid.Empty;
    }
}
