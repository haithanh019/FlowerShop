using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService service) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        LoginDto dto,
        CancellationToken cancellationToken)
        => Ok(await service.LoginAsync(dto, cancellationToken));
}
