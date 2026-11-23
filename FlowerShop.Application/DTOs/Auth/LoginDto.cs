using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Validators;

namespace FlowerShop.Application.DTOs.Auth;

public sealed class LoginDto
{
    [Required(ErrorMessage = "EMAIL_REQUIRED")]
    [NormalizedEmail]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "PASSWORD_REQUIRED")]
    public string Password { get; set; } = string.Empty;
}
