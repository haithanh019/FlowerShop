
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/orders/checkout")]
[Authorize(Roles = "Customer")]
public sealed class CheckoutController(
    ICheckoutService checkoutService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(
        typeof(CheckoutResultDto),
        StatusCodes.Status201Created)]
    [ProducesResponseType(
        typeof(CheckoutResultDto),
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
    public async Task<IActionResult> Checkout(
        [FromBody] CheckoutRequestDto dto,
        CancellationToken cancellationToken)
    {
        // UserId must come from the authenticated JWT.
        if (!Guid.TryParse(
            User.FindFirst("sub")?.Value,
            out var userId) || userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var result = await checkoutService.CheckoutAsync(
            userId,
            dto,
            cancellationToken);

        // Same CheckoutRequestId: return the existing order.
        if (result.IsReplay)
            return Ok(result);

        // A new order has been created successfully.
        return StatusCode(
            StatusCodes.Status201Created,
            result);
    }
}
