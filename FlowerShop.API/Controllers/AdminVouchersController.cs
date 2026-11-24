using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/admin/vouchers")]
[Authorize(Roles = "Admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class AdminVouchersController(
    IAdminVoucherQueryService queries,
    IVoucherService commands) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(AdminVoucherPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPage(
        [FromQuery] string? codeKeyword = null,
        [FromQuery] DiscountType? discountType = null,
        [FromQuery] VoucherAvailabilityStatus? status = null,
        [FromQuery] VoucherSortOrder sort = VoucherSortOrder.Newest,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAdminUserId(out var userId)) return Unauthorized();
        return Ok(await queries.GetPageAsync(userId, codeKeyword, discountType,
            status, sort, page, pageSize, cancellationToken));
    }

    [HttpGet("{voucherId:guid}")]
    [ProducesResponseType(typeof(AdminVoucherViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById([FromRoute] Guid voucherId,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminUserId(out var userId)) return Unauthorized();
        return Ok(await queries.GetDetailAsync(userId, voucherId, cancellationToken));
    }

    [HttpPost]
    [ProducesResponseType(typeof(VoucherDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateVoucherDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminUserId(out var userId)) return Unauthorized();
        var created = await commands.CreateAsync(userId, dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { voucherId = created.VoucherId }, created);
    }

    [HttpPut("{voucherId:guid}")]
    [ProducesResponseType(typeof(VoucherDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update([FromRoute] Guid voucherId,
        [FromBody] UpdateVoucherDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetAdminUserId(out var userId)) return Unauthorized();
        return Ok(await commands.UpdateAsync(userId, voucherId, dto, cancellationToken));
    }

    [HttpPatch("{voucherId:guid}/status")]
    [ProducesResponseType(typeof(VoucherDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateStatus([FromRoute] Guid voucherId,
        [FromBody] UpdateVoucherStatusDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetAdminUserId(out var userId)) return Unauthorized();
        return Ok(await commands.UpdateStatusAsync(userId, voucherId, dto, cancellationToken));
    }

    private bool TryGetAdminUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst("sub")?.Value, out userId) && userId != Guid.Empty;
}
