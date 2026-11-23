using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.DTOs.Auth;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class RegisterDtoValidationTests
{
    [Fact]
    public void RegisterDto_ValidInput_ShouldPass()
    {
        var dto = new RegisterDto
        {
            FullName = "Nguyen Van A",
            Email = "customer@example.com",
            Password = "FlowerShop#2026",
            ConfirmPassword = "FlowerShop#2026"
        };

        var errors = Validate(dto);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(
        "",
        "customer@example.com",
        "FlowerShop#2026",
        "FlowerShop#2026")]
    [InlineData(
        "A",
        "customer@example.com",
        "FlowerShop#2026",
        "FlowerShop#2026")]
    [InlineData(
        "A ",
        "customer@example.com",
        "FlowerShop#2026",
        "FlowerShop#2026")]
    [InlineData(
        "Customer",
        "invalid-email",
        "FlowerShop#2026",
        "FlowerShop#2026")]
    [InlineData(
        "Customer",
        "customer@example.com",
        "short",
        "short")]
    [InlineData(
        "Customer",
        "customer@example.com",
        "FlowerShop#2026",
        "Different#2026")]
    public void RegisterDto_InvalidInput_ShouldFail(
        string fullName,
        string email,
        string password,
        string confirmPassword)
    {
        var dto = new RegisterDto
        {
            FullName = fullName,
            Email = email,
            Password = password,
            ConfirmPassword = confirmPassword
        };

        var errors = Validate(dto);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void RegisterDto_OverlongFields_ShouldFail()
    {
        // Isolate each validation failure. Class-level IValidatableObject.Validate()
        // is not called when another property-level validation has already failed.
        static RegisterDto ValidDto() => new()
        {
            FullName = "Nguyen Van A",
            Email = "customer@example.com",
            Password = "FlowerShop#2026",
            ConfirmPassword = "FlowerShop#2026"
        };

        var invalidName = ValidDto();
        invalidName.FullName = new string('A', 151);
        Assert.Contains(
            Validate(invalidName),
            x => x.ErrorMessage == "FULL_NAME_TOO_LONG" &&
                 x.MemberNames.Contains(nameof(RegisterDto.FullName)));

        var invalidEmail = ValidDto();
        invalidEmail.Email = $"{new string('a', 250)}@example.com";
        Assert.Contains(
            Validate(invalidEmail),
            x => x.ErrorMessage == "EMAIL_TOO_LONG" &&
                 x.MemberNames.Contains(nameof(RegisterDto.Email)));

        var invalidPassword = ValidDto();
        invalidPassword.Password = new string('P', 129);
        invalidPassword.ConfirmPassword = invalidPassword.Password;
        Assert.Contains(
            Validate(invalidPassword),
            x => x.ErrorMessage == "PASSWORD_LENGTH_INVALID" &&
                 x.MemberNames.Contains(nameof(RegisterDto.Password)));
    }

    [Fact]
    public void RegisterDto_WhitespaceOnlyName_ShouldFail()
    {
        var dto = new RegisterDto
        {
            FullName = "   ",
            Email = "customer@example.com",
            Password = "FlowerShop#2026",
            ConfirmPassword = "FlowerShop#2026"
        };

        var errors = Validate(dto);

        Assert.NotEmpty(errors);
    }

    private static List<ValidationResult> Validate(RegisterDto dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(
            dto,
            new ValidationContext(dto),
            results,
            validateAllProperties: true);
        return results;
    }
}
