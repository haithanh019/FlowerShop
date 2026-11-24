using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/vouchers")]
[Authorize(Roles = "Customer")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class VouchersController(
    ICustomerVoucherQueryService queries,
    IVoucherPreviewService preview) : ControllerBase
{
    [HttpGet("available")]
    [ProducesResponseType(typeof(CustomerVoucherPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailable([FromQuery] int page = 1,
        [FromQuery] int pageSize = 10, CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await queries.GetAvailableAsync(userId, page, pageSize, cancellationToken));
    }

    [HttpPost("preview")]
    [ProducesResponseType(typeof(VoucherPreviewResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Preview([FromBody] VoucherPreviewRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await preview.PreviewAsync(userId, dto, cancellationToken));
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst("sub")?.Value, out userId) && userId != Guid.Empty;
}
