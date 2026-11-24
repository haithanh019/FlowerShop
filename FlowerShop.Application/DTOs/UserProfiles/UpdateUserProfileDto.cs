using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace FlowerShop.Application.DTOs.UserProfiles;

public sealed partial class UpdateUserProfileDto : IValidatableObject
{
    public string FullName { get; set; } = string.Empty;

    public string? PhoneNumber { get; set; }

    public IEnumerable<ValidationResult> Validate(
        ValidationContext validationContext)
    {
        // Match RegisterDto and the Users column constraints.
        if (string.IsNullOrWhiteSpace(FullName))
        {
            yield return new ValidationResult(
                "FULL_NAME_REQUIRED",
                [nameof(FullName)]);
        }
        else if (FullName.Trim().Length < 2)
        {
            yield return new ValidationResult(
                "FULL_NAME_TOO_SHORT",
                [nameof(FullName)]);
        }
        else if (FullName.Trim().Length > 150)
        {
            yield return new ValidationResult(
                "FULL_NAME_TOO_LONG",
                [nameof(FullName)]);
        }

        // Phone is optional. The service will normalize blank to null.
        if (!string.IsNullOrWhiteSpace(PhoneNumber))
        {
            var phone = PhoneNumber.Trim();
            var digitCount = phone.Count(c => c is >= '0' and <= '9');

            if (phone.Length > 20)
            {
                yield return new ValidationResult(
                    "PHONE_NUMBER_TOO_LONG",
                    [nameof(PhoneNumber)]);
            }
            else if (digitCount < 7 || !PhoneNumberRegex().IsMatch(phone))
            {
                yield return new ValidationResult(
                    "PHONE_NUMBER_INVALID",
                    [nameof(PhoneNumber)]);
            }
        }
    }

    [GeneratedRegex(@"^\+?[0-9][0-9 ().-]*$")]
    private static partial Regex PhoneNumberRegex();
}
