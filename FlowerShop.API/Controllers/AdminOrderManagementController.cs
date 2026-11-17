using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/admin/orders")]
[Authorize(Roles = "Admin")]
public sealed class AdminOrderManagementController(
    IAdminOrderStatusService statusService,
    IOrderCancellationService cancellationService)
    : ControllerBase
{
    // PATCH /api/admin/orders/{orderId}/status
    [HttpPatch("{orderId:guid}/status")]
    [ProducesResponseType(
        typeof(OrderStatusUpdateResultDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(
        StatusCodes.Status403Forbidden)]
    [ProducesResponseType(
        StatusCodes.Status404NotFound)]
    [ProducesResponseType(
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateStatus(
        Guid orderId,
        [FromBody] UpdateOrderStatusRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminUserId(out var adminUserId))
            return Unauthorized();

        // [ApiController] automatically rejects null,
        // missing, or invalid Status values.
        var result = await statusService.UpdateStatusAsync(
            adminUserId,
            orderId,
            dto.Status!.Value,
            cancellationToken);

        return Ok(result);
    }

    // POST /api/admin/orders/{orderId}/cancel
    [HttpPost("{orderId:guid}/cancel")]
    [ProducesResponseType(
        typeof(OrderStatusUpdateResultDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(
        StatusCodes.Status403Forbidden)]
    [ProducesResponseType(
        StatusCodes.Status404NotFound)]
    [ProducesResponseType(
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminUserId(out var adminUserId))
            return Unauthorized();

        var result =
            await cancellationService.CancelByAdminAsync(
                adminUserId,
                orderId,
                cancellationToken);

        return Ok(result);
    }

    private bool TryGetAdminUserId(out Guid userId)
    {
        return Guid.TryParse(
            User.FindFirst("sub")?.Value,
            out userId)
            && userId != Guid.Empty;
    }
}
