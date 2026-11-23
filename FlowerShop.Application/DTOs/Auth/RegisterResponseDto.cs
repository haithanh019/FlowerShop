namespace FlowerShop.Application.DTOs.Auth;

public sealed record RegisterResponseDto(
    Guid UserId,
    string FullName,
    string Email,
    DateTime CreatedAtUtc);
