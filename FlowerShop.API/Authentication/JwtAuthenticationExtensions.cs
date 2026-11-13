using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using FlowerShop.Application.Interfaces.Services;

namespace FlowerShop.API.Authentication;

public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];
        var key = configuration["Jwt:Key"];

        if (string.IsNullOrWhiteSpace(issuer) ||
            string.IsNullOrWhiteSpace(audience) ||
            string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "JWT configuration is missing.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);

        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "JWT signing key must be at least 32 bytes.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;

                options.TokenValidationParameters =
                    new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = issuer,

                        ValidateAudience = true,
                        ValidAudience = audience,

                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey =
                            new SymmetricSecurityKey(keyBytes),

                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        RequireSignedTokens = true,

                        ClockSkew = TimeSpan.FromSeconds(30),

                        NameClaimType = "sub",
                        RoleClaimType = "role"
                    };
            });

        services.AddAuthorization();
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        return services;
    }
}
