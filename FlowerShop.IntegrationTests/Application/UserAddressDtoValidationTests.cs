using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.DTOs.UserAddresses;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class UserAddressDtoValidationTests
{
    [Fact]
    public void ValidAddress_ShouldPassValidation()
    {
        var dto = ValidAddress();

        Assert.Empty(Validate(dto));
    }

    [Theory]
    [InlineData(nameof(UserAddressUpsertDto.RecipientName))]
    [InlineData(nameof(UserAddressUpsertDto.RecipientPhone))]
    [InlineData(nameof(UserAddressUpsertDto.AddressLine))]
    [InlineData(nameof(UserAddressUpsertDto.Ward))]
    [InlineData(nameof(UserAddressUpsertDto.District))]
    [InlineData(nameof(UserAddressUpsertDto.Province))]
    public void MissingRequiredField_ShouldFail(
        string fieldName)
    {
        var dto = ValidAddress();

        // Change only the field under test.
        typeof(UserAddressUpsertDto)
            .GetProperty(fieldName)!
            .SetValue(dto, "   ");

        Assert.Contains(
            Validate(dto),
            x => x.MemberNames.Contains(fieldName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("V")]
    [InlineData("VNM")]
    [InlineData("12")]
    [InlineData("V1")]
    public void InvalidCountryCode_ShouldFail(
        string countryCode)
    {
        var dto = ValidAddress();
        dto.CountryCode = countryCode;

        Assert.Contains(
            Validate(dto),
            x => x.MemberNames.Contains(
                nameof(UserAddressUpsertDto.CountryCode)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("123")]
    [InlineData("123456789012345678901")]
    public void InvalidPhone_ShouldFail(
        string phone)
    {
        var dto = ValidAddress();
        dto.RecipientPhone = phone;

        Assert.Contains(
            Validate(dto),
            x => x.MemberNames.Contains(
                nameof(UserAddressUpsertDto.RecipientPhone)));
    }

    [Fact]
    public void OverlongFields_ShouldFail()
    {
        var dto = ValidAddress();

        dto.RecipientName = new string('A', 151);
        dto.AddressLine = new string('B', 301);
        dto.Ward = new string('C', 101);
        dto.PostalCode = new string('1', 21);

        var errors = Validate(dto);

        Assert.Contains(errors,
            x => x.MemberNames.Contains(
                nameof(UserAddressUpsertDto.RecipientName)));

        Assert.Contains(errors,
            x => x.MemberNames.Contains(
                nameof(UserAddressUpsertDto.AddressLine)));

        Assert.Contains(errors,
            x => x.MemberNames.Contains(
                nameof(UserAddressUpsertDto.Ward)));

        Assert.Contains(errors,
            x => x.MemberNames.Contains(
                nameof(UserAddressUpsertDto.PostalCode)));
    }

    [Fact]
    public void UpsertDto_ShouldNotExposeOwnershipOrDefaultFields()
    {
        Assert.Null(
            typeof(UserAddressUpsertDto)
                .GetProperty("UserId"));

        Assert.Null(
            typeof(UserAddressUpsertDto)
                .GetProperty("IsDefault"));

        Assert.Null(
            typeof(UserAddressUpsertDto)
                .GetProperty("Id"));
    }

    private static UserAddressUpsertDto ValidAddress()
    {
        return new UserAddressUpsertDto
        {
            RecipientName = "  Nguyen Van A  ",
            RecipientPhone = "0901234567",
            AddressLine = "123 Nguyen Van Linh",
            Ward = "Hai Chau",
            District = "Hai Chau",
            Province = "Da Nang",
            CountryCode = " vn ",
            PostalCode = "550000"
        };
    }

    private static List<ValidationResult> Validate(
        UserAddressUpsertDto dto)
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
