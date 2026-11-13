
using System.Security.Claims;
using System.Text;
using FlowerShop.Application.DTOs.Auth;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FlowerShop.API.Authentication;

public sealed class JwtTokenService(
    IConfiguration configuration) : IJwtTokenService
{
    public LoginResponseDto CreateToken(User user)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(15);

        var key = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Missing JWT key.");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = configuration["Jwt:Issuer"],
            Audience = configuration["Jwt:Audience"],
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,

            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("role", user.Role.ToString()),
                new Claim("jti", Guid.NewGuid().ToString())
            ]),

            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256)
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        return new LoginResponseDto(token, expires);
    }
}
