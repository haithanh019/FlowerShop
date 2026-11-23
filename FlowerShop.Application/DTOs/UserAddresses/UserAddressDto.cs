namespace FlowerShop.Application.DTOs.UserAddresses;

public sealed record UserAddressDto(
    Guid AddressId,
    string RecipientName,
    string RecipientPhone,
    string AddressLine,
    string Ward,
    string District,
    string Province,
    string CountryCode,
    string? PostalCode,
    bool IsDefault,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);
