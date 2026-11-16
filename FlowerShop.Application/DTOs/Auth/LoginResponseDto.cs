namespace FlowerShop.Application.DTOs.Auth;

public sealed record LoginResponseDto(
    string AccessToken,
    DateTime ExpiresAtUtc,
    string TokenType = "Bearer");