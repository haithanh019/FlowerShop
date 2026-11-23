using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Auth;

public sealed class RegisterDto : IValidatableObject
{
    [Required(ErrorMessage = "FULL_NAME_REQUIRED")]
    [StringLength(
        150,
        ErrorMessage = "FULL_NAME_TOO_LONG")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "EMAIL_REQUIRED")]
    [EmailAddress(ErrorMessage = "EMAIL_INVALID")]
    [StringLength(
        256,
        ErrorMessage = "EMAIL_TOO_LONG")]
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
        // Validate the normalized name length.
        // "A " must not pass the 2-character rule.
        if (!string.IsNullOrWhiteSpace(FullName) &&
            FullName.Trim().Length < 2)
        {
            yield return new ValidationResult(
                "FULL_NAME_TOO_SHORT",
                [nameof(FullName)]);
        }
    }
}
