using FlowerShop.Application.DTOs.ProductImages;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/products/{productId:guid}/images")]
[Authorize(Roles = "Admin")]
public sealed class ProductImagesController(
    IProductImageService service, IProductImageQueryService queryService) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        [FromRoute] Guid productId,
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null)
            throw new ValidationException("IMAGE_REQUIRED");

        await using var stream = file.OpenReadStream();

        var result = await service.UploadAsync(
            productId,
            stream,
            file.FileName,
            file.ContentType,
            file.Length,
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpDelete("{imageId:guid}")]
    public async Task<IActionResult> Delete(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        await service.DeleteAsync(
            productId, imageId, cancellationToken);

        return NoContent();
    }

    [HttpPut("{imageId:guid}/primary")]
    public async Task<IActionResult> SetPrimary(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        await service.SetPrimaryAsync(
            productId, imageId, cancellationToken);

        return NoContent();
    }

    [HttpPut("order")]
    public async Task<IActionResult> Reorder(
        Guid productId,
        [FromBody] ReorderProductImagesDto dto,
        CancellationToken cancellationToken)
    {
        await service.ReorderAsync(
            productId, dto.ImageIds, cancellationToken);

        return NoContent();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublic(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var images = await queryService.GetPublicAsync(
            productId, cancellationToken);

        return Ok(images);
    }

    [HttpGet("/api/admin/products/{productId:guid}/images")]
    public async Task<IActionResult> GetAdmin(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var images = await queryService.GetAdminAsync(
            productId, cancellationToken);

        return Ok(images);
    }


}
