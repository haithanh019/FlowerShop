namespace FlowerShop.Application.DTOs.UserProfiles;

public sealed record UserProfileDto(
    Guid UserId,
    string FullName,
    string Email,
    string? PhoneNumber,
    bool IsEmailVerified,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);
