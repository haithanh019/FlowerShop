using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.DTOs.UserProfiles;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class UserProfileDtoValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0901234567")]
    [InlineData(" +84 901 234 567 ")]
    [InlineData("1234567")]
    [InlineData("12345678901234567890")]
    public void ValidProfile_ShouldPass(string? phoneNumber)
    {
        var dto = new UpdateUserProfileDto
        {
            FullName = "  Nguyen Van A  ",
            PhoneNumber = phoneNumber
        };

        Assert.Empty(Validate(dto));
    }

    [Theory]
    [InlineData(null, "FULL_NAME_REQUIRED")]
    [InlineData("", "FULL_NAME_REQUIRED")]
    [InlineData("   ", "FULL_NAME_REQUIRED")]
    [InlineData("A", "FULL_NAME_TOO_SHORT")]
    [InlineData(" A ", "FULL_NAME_TOO_SHORT")]
    public void InvalidFullName_ShouldFail(string? name, string errorCode)
    {
        var dto = new UpdateUserProfileDto
        {
            FullName = name!,
            PhoneNumber = "0901234567"
        };

        Assert.Contains(Validate(dto), x =>
            x.ErrorMessage == errorCode &&
            x.MemberNames.Contains(nameof(UpdateUserProfileDto.FullName)));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(150, true)]
    [InlineData(151, false)]
    public void FullNameLength_ShouldValidateAfterTrim(int length, bool valid)
    {
        var dto = new UpdateUserProfileDto
        {
            FullName = $"  {new string('A', length)}  "
        };

        var errors = Validate(dto);

        if (valid)
            Assert.Empty(errors);
        else
            Assert.Contains(errors, x =>
                x.ErrorMessage == "FULL_NAME_TOO_LONG" &&
                x.MemberNames.Contains(nameof(UpdateUserProfileDto.FullName)));
    }

    [Theory]
    [InlineData("abc", "PHONE_NUMBER_INVALID")]
    [InlineData("123456", "PHONE_NUMBER_INVALID")]
    [InlineData("123-456-", "PHONE_NUMBER_INVALID")]
    [InlineData("090123456X", "PHONE_NUMBER_INVALID")]
    [InlineData("090+1234567", "PHONE_NUMBER_INVALID")]
    [InlineData("123456789012345678901", "PHONE_NUMBER_TOO_LONG")]
    public void InvalidPhone_ShouldFail(string phoneNumber, string errorCode)
    {
        var dto = new UpdateUserProfileDto
        {
            FullName = "Nguyen Van A",
            PhoneNumber = phoneNumber
        };

        Assert.Contains(Validate(dto), x =>
            x.ErrorMessage == errorCode &&
            x.MemberNames.Contains(nameof(UpdateUserProfileDto.PhoneNumber)));
    }

    [Fact]
    public void MultipleInvalidFields_ShouldReportBothErrors()
    {
        var dto = new UpdateUserProfileDto
        {
            FullName = new string('A', 151),
            PhoneNumber = "abc"
        };

        var errors = Validate(dto);

        Assert.Contains(errors, x =>
            x.ErrorMessage == "FULL_NAME_TOO_LONG" &&
            x.MemberNames.Contains(nameof(UpdateUserProfileDto.FullName)));

        Assert.Contains(errors, x =>
            x.ErrorMessage == "PHONE_NUMBER_INVALID" &&
            x.MemberNames.Contains(nameof(UpdateUserProfileDto.PhoneNumber)));
    }

    [Fact]
    public void UpdateDto_ShouldExposeOnlyEditableFields()
    {
        var properties = typeof(UpdateUserProfileDto).GetProperties();

        Assert.Equal(2, properties.Length);
        Assert.Contains(properties, x => x.Name == nameof(UpdateUserProfileDto.FullName));
        Assert.Contains(properties, x => x.Name == nameof(UpdateUserProfileDto.PhoneNumber));
    }

    [Fact]
    public void ProfileResponse_ShouldNotExposeSensitiveFields()
    {
        var properties = typeof(UserProfileDto).GetProperties();

        Assert.DoesNotContain(properties, x =>
            x.Name is "Password" or "PasswordHash" or "Role" or "IsActive");
    }

    private static List<ValidationResult> Validate(UpdateUserProfileDto dto)
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
