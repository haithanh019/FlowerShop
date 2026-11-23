using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.Validators;

[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class NormalizedEmailAttribute
    : ValidationAttribute
{
    private const int MaxEmailLength = 256;

    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext)
    {
        var memberNames = new[]
        {
            validationContext.MemberName ?? "Email"
        };

        if (value is not string rawEmail ||
            string.IsNullOrWhiteSpace(rawEmail))
        {
            return new ValidationResult(
                "EMAIL_REQUIRED",
                memberNames);
        }

        // Validation must use the same normalized value
        // that AuthService uses for persistence and lookup.
        var email = rawEmail.Trim();

        if (email.Length > MaxEmailLength)
        {
            return new ValidationResult(
                "EMAIL_TOO_LONG",
                memberNames);
        }

        if (!new EmailAddressAttribute().IsValid(email))
        {
            return new ValidationResult(
                "EMAIL_INVALID",
                memberNames);
        }

        return ValidationResult.Success;
    }
}
