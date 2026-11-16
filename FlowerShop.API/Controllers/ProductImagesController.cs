using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/products/{productId:guid}/images")]
[Authorize(Roles = "Admin")]
public sealed class ProductImagesController(
    IProductImageService service) : ControllerBase
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
}
