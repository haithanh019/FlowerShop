using FlowerShop.Application.DTOs.Carts;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/cart")]
[Authorize]
public sealed class CartController(
    ICartQueryService queryService,
    ICartService cartService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await queryService.GetByUserIdAsync(
            userId, cancellationToken);

        return Ok(result);
    }

    [HttpPost("items")]
    public async Task<IActionResult> AddItem(
        [FromBody] AddCartItemDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        await cartService.AddItemAsync(
            userId, dto, cancellationToken);

        return NoContent();
    }

    [HttpPut("items/{cartItemId:guid}")]
    public async Task<IActionResult> UpdateQuantity(
        Guid cartItemId,
        [FromBody] UpdateCartItemDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        await cartService.UpdateQuantityAsync(
            userId, cartItemId, dto, cancellationToken);

        return NoContent();
    }

    [HttpDelete("items/{cartItemId:guid}")]
    public async Task<IActionResult> RemoveItem(
        Guid cartItemId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        await cartService.RemoveItemAsync(
            userId, cartItemId, cancellationToken);

        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        return Guid.TryParse(
            User.FindFirst("sub")?.Value,
            out userId)
            && userId != Guid.Empty;
    }
}
