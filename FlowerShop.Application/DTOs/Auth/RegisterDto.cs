using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Validators;

namespace FlowerShop.Application.DTOs.Auth;

public sealed class RegisterDto : IValidatableObject
{
    [Required(ErrorMessage = "FULL_NAME_REQUIRED")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "EMAIL_REQUIRED")]
    [NormalizedEmail]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "PASSWORD_REQUIRED")]
    [StringLength(
        128,
        MinimumLength = 8,
        ErrorMessage = "PASSWORD_LENGTH_INVALID")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "CONFIRM_PASSWORD_REQUIRED")]
    [Compare(
        nameof(Password),
        ErrorMessage = "PASSWORD_CONFIRMATION_MISMATCH")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(
        ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(FullName))
            yield break;

        var normalizedName = FullName.Trim();

        if (normalizedName.Length < 2)
        {
            yield return new ValidationResult(
                "FULL_NAME_TOO_SHORT",
                [nameof(FullName)]);
        }
        else if (normalizedName.Length > 150)
        {
            yield return new ValidationResult(
                "FULL_NAME_TOO_LONG",
                [nameof(FullName)]);
        }
    }
}
