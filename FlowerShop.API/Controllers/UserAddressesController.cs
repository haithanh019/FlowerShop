using FlowerShop.Application.DTOs.UserAddresses;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/me/addresses")]
[Authorize(Roles = "Customer")]
public sealed class UserAddressesController(
    IUserAddressService addressService) : ControllerBase
{
    // GET /api/me/addresses
    [HttpGet]
    [ProducesResponseType(
        typeof(IReadOnlyList<UserAddressDto>),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAll(
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        var result = await addressService.GetAllAsync(
            userId, cancellationToken);

        return Ok(result);
    }

    // GET /api/me/addresses/{addressId}
    [HttpGet("{addressId:guid}")]
    [ProducesResponseType(
        typeof(UserAddressDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] Guid addressId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        var result = await addressService.GetByIdAsync(
            userId, addressId, cancellationToken);

        return Ok(result);
    }

    // POST /api/me/addresses
    [HttpPost]
    [ProducesResponseType(
        typeof(UserAddressDto),
        StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] UserAddressUpsertDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        var created = await addressService.CreateAsync(
            userId, dto, cancellationToken);

        return CreatedAtAction(
            nameof(GetById),
            new { addressId = created.AddressId },
            created);
    }

    // PUT /api/me/addresses/{addressId}
    [HttpPut("{addressId:guid}")]
    [ProducesResponseType(
        typeof(UserAddressDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        [FromRoute] Guid addressId,
        [FromBody] UserAddressUpsertDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        var updated = await addressService.UpdateAsync(
            userId, addressId, dto, cancellationToken);

        return Ok(updated);
    }

    // DELETE /api/me/addresses/{addressId}
    [HttpDelete("{addressId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        [FromRoute] Guid addressId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        await addressService.DeleteAsync(
            userId, addressId, cancellationToken);

        return NoContent();
    }

    // PATCH /api/me/addresses/{addressId}/default
    [HttpPatch("{addressId:guid}/default")]
    [ProducesResponseType(
        typeof(UserAddressDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetDefault(
        [FromRoute] Guid addressId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        var result = await addressService.SetDefaultAsync(
            userId, addressId, cancellationToken);

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
