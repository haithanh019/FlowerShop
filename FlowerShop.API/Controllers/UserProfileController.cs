using FlowerShop.Application.DTOs.UserProfiles;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/me/profile")]
[Authorize(Roles = "Customer")]
[ResponseCache(
    NoStore = true,
    Location = ResponseCacheLocation.None)]
public sealed class UserProfileController(
    IUserProfileService profileService) : ControllerBase
{
    // GET /api/me/profile
    [HttpGet]
    [ProducesResponseType(
        typeof(UserProfileDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        var result = await profileService.GetAsync(
            userId, cancellationToken);

        return Ok(result);
    }

    // PUT /api/me/profile
    [HttpPut]
    [ProducesResponseType(
        typeof(UserProfileDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        [FromBody] UpdateUserProfileDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetCustomerUserId(out var userId))
            return Unauthorized();

        var result = await profileService.UpdateAsync(
            userId, dto, cancellationToken);

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
