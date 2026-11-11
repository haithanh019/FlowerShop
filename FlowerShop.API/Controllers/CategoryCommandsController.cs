
using FlowerShop.Application.DTOs.Categories;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/categories")]
[Authorize(Roles = "Admin")]
public sealed class CategoryCommandsController(
    ICategoryService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateCategoryDto dto,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(dto, cancellationToken);

        return Created($"/odata/v1/Categories({result.Id})", result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateCategoryDto dto,
        CancellationToken cancellationToken)
        => Ok(await service.UpdateAsync(id, dto, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
