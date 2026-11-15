using FlowerShop.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace FlowerShop.API.Authentication;

public sealed class JwtUserValidationEvents(
    IUserAccessService userAccessService) : JwtBearerEvents
{
    public override async Task TokenValidated(
        TokenValidatedContext context)
    {
        var subject = context.Principal?
            .FindFirst("sub")?.Value;

        var role = context.Principal?
            .FindFirst("role")?.Value;

        if (!Guid.TryParse(subject, out var userId)
            || string.IsNullOrWhiteSpace(role)
            || !await userAccessService.IsActiveWithRoleAsync(
                userId,
                role,
                context.HttpContext.RequestAborted))
        {
            context.Fail("Token is no longer valid.");
        }
    }
}
