using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace FlowerShop.Application.DTOs.UserAddresses;

public sealed class UserAddressUpsertDto : IValidatableObject
{
    public string RecipientName { get; set; } = string.Empty;

    public string RecipientPhone { get; set; } = string.Empty;

    public string AddressLine { get; set; } = string.Empty;

    public string Ward { get; set; } = string.Empty;

    public string District { get; set; } = string.Empty;

    public string Province { get; set; } = string.Empty;

    public string CountryCode { get; set; } = string.Empty;

    public string? PostalCode { get; set; }

    public IEnumerable<ValidationResult> Validate(
        ValidationContext validationContext)
    {
        // Match the database column constraints,
        // but validate strings after Trim().
        var fields = new[]
        {
            (
                Property: nameof(RecipientName),
                Code: "RECIPIENT_NAME",
                Value: RecipientName,
                Max: 150
            ),
            (
                Property: nameof(RecipientPhone),
                Code: "RECIPIENT_PHONE",
                Value: RecipientPhone,
                Max: 20
            ),
            (
                Property: nameof(AddressLine),
                Code: "ADDRESS_LINE",
                Value: AddressLine,
                Max: 300
            ),
            (
                Property: nameof(Ward),
                Code: "WARD",
                Value: Ward,
                Max: 100
            ),
            (
                Property: nameof(District),
                Code: "DISTRICT",
                Value: District,
                Max: 100
            ),
            (
                Property: nameof(Province),
                Code: "PROVINCE",
                Value: Province,
                Max: 100
            )
        };

        // 1. Required fields and maximum lengths.
        foreach (var (property, code, value, max) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return new ValidationResult(
                    $"{code}_REQUIRED",
                    [property]);

                continue;
            }

            if (value.Trim().Length > max)
            {
                yield return new ValidationResult(
                    $"{code}_TOO_LONG",
                    [property]);
            }
        }

        // 2. Recipient name minimum length.
        if (!string.IsNullOrWhiteSpace(RecipientName) &&
            RecipientName.Trim().Length < 2)
        {
            yield return new ValidationResult(
                "RECIPIENT_NAME_TOO_SHORT",
                [nameof(RecipientName)]);
        }
    }
}