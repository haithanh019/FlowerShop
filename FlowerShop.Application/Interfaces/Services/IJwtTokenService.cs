using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Domain.Entities;

namespace FlowerShop.Application.Interfaces.Services;

public interface IJwtTokenService
{
    LoginResponseDto CreateToken(User user);
}