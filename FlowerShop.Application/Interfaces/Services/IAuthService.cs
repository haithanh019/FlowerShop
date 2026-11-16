using FlowerShop.Application.DTOs.Auth;

namespace FlowerShop.Application.Interfaces.Services;

public interface IAuthService
{
    Task<LoginResponseDto> LoginAsync(
        LoginDto dto,
        CancellationToken cancellationToken = default);
}