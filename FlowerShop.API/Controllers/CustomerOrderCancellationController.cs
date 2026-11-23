using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize(Roles = "Customer")]
public sealed class CustomerOrderCancellationController(
    IOrderCancellationService cancellationService)
    : ControllerBase
{
    // POST /api/orders/{orderId}/cancel
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
        if (!TryGetCustomerUserId(out var customerUserId))
            return Unauthorized();

        var result =
            await cancellationService.CancelByCustomerAsync(
                customerUserId,
                orderId,
                cancellationToken);

        return Ok(result);
    }

    private bool TryGetCustomerUserId(out Guid userId)
    {
        return Guid.TryParse(
            User.FindFirst("sub")?.Value,
            out userId)
            && userId != Guid.Empty;
    }
}
