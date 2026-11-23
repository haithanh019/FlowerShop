using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/admin/orders")]
[Authorize(Roles = "Admin")]
public sealed class AdminOrdersQueryController(
    IAdminOrderQueryService service) : ControllerBase
{
    // GET /api/admin/orders
    [HttpGet]
    [ProducesResponseType(
        typeof(AdminOrderPageDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(
        StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPage(
        [FromQuery] OrderStatus? status = null,
        [FromQuery] Guid? customerUserId = null,
        [FromQuery] string? orderCodeKeyword = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAdminUserId(out var adminUserId))
            return Unauthorized();

        var result = await service.GetPageAsync(
            adminUserId,
            status,
            customerUserId,
            orderCodeKeyword,
            page,
            pageSize,
            cancellationToken);

        return Ok(result);
    }

    // GET /api/admin/orders/{orderId}
    [HttpGet("{orderId:guid}")]
    [ProducesResponseType(
        typeof(AdminOrderDetailDto),
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
        if (!TryGetAdminUserId(out var adminUserId))
            return Unauthorized();

        var result = await service.GetDetailAsync(
            adminUserId,
            orderId,
            cancellationToken);

        return Ok(result);
    }

    private bool TryGetAdminUserId(out Guid adminUserId)
    {
        return Guid.TryParse(
            User.FindFirst("sub")?.Value,
            out adminUserId)
            && adminUserId != Guid.Empty;
    }
}
