
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FlowerShop.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IAuthService service) : ControllerBase
{
    // POST /api/auth/login
    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType(
        typeof(LoginResponseDto),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(
        StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(
        [FromBody] LoginDto dto,
        CancellationToken cancellationToken)
    {
        var result = await service.LoginAsync(
            dto,
            cancellationToken);

        return Ok(result);
    }

    // POST /api/auth/register
    [AllowAnonymous]
    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    [ProducesResponseType(
        typeof(RegisterResponseDto),
        StatusCodes.Status201Created)]
    [ProducesResponseType(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        StatusCodes.Status409Conflict)]
    [ProducesResponseType(
        StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterDto dto,
        CancellationToken cancellationToken)
    {
        var result = await service.RegisterAsync(
            dto,
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created,
            result);
    }
}
