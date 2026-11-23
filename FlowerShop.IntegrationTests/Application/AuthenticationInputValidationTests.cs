using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.DTOs.Auth;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class AuthenticationInputValidationTests
{
    [Fact]
    public void Register_ShouldAcceptTrimmedNameAndEmail()
    {
        var dto = new RegisterDto
        {
            FullName = "  Nguyen Van A  ",
            Email = "  CUSTOMER@EXAMPLE.COM  ",
            Password = "FlowerShop#2026",
            ConfirmPassword = "FlowerShop#2026"
        };

        Assert.Empty(Validate(dto));
    }

    [Fact]
    public void Login_ShouldAcceptEmailWithOuterWhitespace()
    {
        var dto = new LoginDto
        {
            Email = "  CUSTOMER@EXAMPLE.COM  ",
            Password = "FlowerShop#2026"
        };

        Assert.Empty(Validate(dto));
    }

    [Fact]
    public void Register_ShouldRejectEmailLongerThan256AfterTrim()
    {
        var dto = ValidRegistration();

        dto.Email =
            $"  {new string('a', 250)}@example.com  ";

        Assert.Contains(
            Validate(dto),
            x => x.MemberNames.Contains(
                nameof(RegisterDto.Email)));
    }

    [Fact]
    public void Register_ShouldRejectShortNameAfterTrim()
    {
        var dto = ValidRegistration();

        dto.FullName = "  A  ";

        Assert.Contains(
            Validate(dto),
            x => x.MemberNames.Contains(
                nameof(RegisterDto.FullName)));
    }

    [Fact]
    public void Register_ShouldAccept150CharacterTrimmedName()
    {
        var dto = ValidRegistration();

        dto.FullName = $"  {new string('A', 150)}  ";

        Assert.Empty(Validate(dto));
    }

    [Fact]
    public void Login_ShouldRejectInvalidEmailAfterTrim()
    {
        var dto = new LoginDto
        {
            Email = "  invalid-email  ",
            Password = "FlowerShop#2026"
        };

        Assert.Contains(
            Validate(dto),
            x => x.MemberNames.Contains(
                nameof(LoginDto.Email)));
    }

    private static RegisterDto ValidRegistration() => new()
    {
        FullName = "Nguyen Van A",
        Email = "customer@example.com",
        Password = "FlowerShop#2026",
        ConfirmPassword = "FlowerShop#2026"
    };

    private static List<ValidationResult> Validate(
        object dto)
    {
        var errors = new List<ValidationResult>();

        Validator.TryValidateObject(
            dto,
            new ValidationContext(dto),
            errors,
            validateAllProperties: true);

        return errors;
    }
}
