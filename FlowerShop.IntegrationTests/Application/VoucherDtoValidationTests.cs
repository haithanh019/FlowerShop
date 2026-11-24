using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Domain.Enums;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class VoucherDtoValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("flower20", "FLOWER20")]
    [InlineData("  New-Customer_2  ", "NEW-CUSTOMER_2")]
    [InlineData("A", "A")]
    [InlineData("0", "0")]
    public void ValidCodes_ShouldNormalize(string raw, string expected)
    {
        var dto = ValidCreate();
        dto.Code = raw;
        Assert.Empty(Validate(dto));
        Assert.Equal(expected, VoucherInputPolicy.NormalizeCode(raw));
        Assert.Empty(Validate(new VoucherPreviewRequestDto { VoucherCode = raw }));
    }

    [Theory]
    [InlineData(null, "VOUCHER_CODE_REQUIRED")]
    [InlineData("", "VOUCHER_CODE_REQUIRED")]
    [InlineData("   ", "VOUCHER_CODE_REQUIRED")]
    [InlineData("FLOWER SALE", "VOUCHER_CODE_INVALID")]
    [InlineData("ABC\nDEF", "VOUCHER_CODE_INVALID")]
    [InlineData("FLOWER!", "VOUCHER_CODE_INVALID")]
    [InlineData("ſALE", "VOUCHER_CODE_INVALID")]
    [InlineData("ＡＢＣ", "VOUCHER_CODE_INVALID")]
    public void InvalidCodes_ShouldFailForCreateAndPreview(string? raw, string code)
    {
        var dto = ValidCreate();
        dto.Code = raw!;
        ExpectError(dto, code, nameof(CreateVoucherDto.Code));
        ExpectError(new VoucherPreviewRequestDto { VoucherCode = raw! }, code,
            nameof(VoucherPreviewRequestDto.VoucherCode));
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void CodeLength_ShouldValidateAfterTrim(int length, bool valid)
    {
        var dto = ValidCreate();
        dto.Code = $"  {new string('A', length)}  ";
        if (valid) Assert.Empty(Validate(dto));
        else ExpectError(dto, "VOUCHER_CODE_TOO_LONG", nameof(dto.Code));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(500, false)]
    [InlineData(501, true)]
    public void DescriptionLength_ShouldValidateAfterTrim(int length, bool invalid)
    {
        var dto = ValidCreate();
        dto.Description = $"  {new string('D', length)}  ";
        if (invalid) ExpectError(dto, "DESCRIPTION_TOO_LONG", nameof(dto.Description));
        else Assert.Empty(Validate(dto));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t ")]
    public void BlankDescription_ShouldNormalizeToNull(string? raw)
    {
        Assert.Null(VoucherInputPolicy.NormalizeDescription(raw));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void InvalidDiscountType_ShouldFail(int type)
    {
        var dto = ValidCreate();
        dto.DiscountType = (DiscountType)type;
        ExpectError(dto, "DISCOUNT_TYPE_INVALID", nameof(dto.DiscountType));
    }

    [Theory]
    [InlineData(DiscountType.Percentage, "0.01", null)]
    [InlineData(DiscountType.Percentage, "100", null)]
    [InlineData(DiscountType.Percentage, "20.000", null)]
    [InlineData(DiscountType.Percentage, "100.01", "DISCOUNT_VALUE_INVALID")]
    [InlineData(DiscountType.Percentage, "0", "DISCOUNT_VALUE_INVALID")]
    [InlineData(DiscountType.Percentage, "-1", "DISCOUNT_VALUE_INVALID")]
    [InlineData(DiscountType.Percentage, "0.001", "DISCOUNT_VALUE_PRECISION_INVALID")]
    [InlineData(DiscountType.FixedAmount, "9999999999999999.99", null)]
    [InlineData(DiscountType.FixedAmount, "10000000000000000", "DISCOUNT_VALUE_INVALID")]
    [InlineData(DiscountType.FixedAmount, "1.234", "DISCOUNT_VALUE_PRECISION_INVALID")]
    public void DiscountValue_ShouldValidateRangeAndPrecision(DiscountType type, string amount, string? error)
    {
        var dto = ValidCreate();
        dto.DiscountType = type;
        dto.DiscountValue = decimal.Parse(amount, CultureInfo.InvariantCulture);
        if (error is null) Assert.Empty(Validate(dto));
        else ExpectError(dto, error, nameof(dto.DiscountValue));
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("9999999999999999.99", null)]
    [InlineData("10000000000000000", "MIN_ORDER_AMOUNT_INVALID")]
    [InlineData("-0.01", "MIN_ORDER_AMOUNT_INVALID")]
    [InlineData("0.001", "MIN_ORDER_AMOUNT_PRECISION_INVALID")]
    public void MinOrderAmount_ShouldValidate(string amount, string? error)
    {
        var dto = ValidCreate();
        dto.MinOrderAmount = decimal.Parse(amount, CultureInfo.InvariantCulture);
        if (error is null) Assert.Empty(Validate(dto));
        else ExpectError(dto, error, nameof(dto.MinOrderAmount));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0.01", null)]
    [InlineData("9999999999999999.99", null)]
    [InlineData("10000000000000000", "MAX_DISCOUNT_AMOUNT_INVALID")]
    [InlineData("0", "MAX_DISCOUNT_AMOUNT_INVALID")]
    [InlineData("-1", "MAX_DISCOUNT_AMOUNT_INVALID")]
    [InlineData("0.001", "MAX_DISCOUNT_AMOUNT_PRECISION_INVALID")]
    public void PercentageCap_ShouldValidate(string? amount, string? error)
    {
        var dto = ValidCreate();
        dto.MaxDiscountAmount = amount is null ? null : decimal.Parse(amount, CultureInfo.InvariantCulture);
        if (error is null) Assert.Empty(Validate(dto));
        else ExpectError(dto, error, nameof(dto.MaxDiscountAmount));
    }

    [Fact]
    public void FixedAmount_ShouldRejectCap()
    {
        var dto = ValidCreate();
        dto.DiscountType = DiscountType.FixedAmount;
        dto.MaxDiscountAmount = 100m;
        ExpectError(dto, "MAX_DISCOUNT_NOT_ALLOWED_FOR_FIXED", nameof(dto.MaxDiscountAmount));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(int.MaxValue, int.MaxValue, true)]
    [InlineData(0, 1, false)]
    [InlineData(-1, null, false)]
    [InlineData(1, 0, false)]
    [InlineData(null, -1, false)]
    public void UsageLimits_ShouldBePositiveOrNull(int? total, int? perUser, bool valid)
    {
        var dto = ValidCreate();
        dto.UsageLimit = total;
        dto.PerUserLimit = perUser;
        var errors = Validate(dto);
        if (valid) Assert.Empty(errors);
        else
        {
            Assert.NotEmpty(errors);
            if (total is <= 0)
                Assert.Contains(errors, e => e.ErrorMessage == "USAGE_LIMIT_INVALID");
            if (perUser is <= 0)
                Assert.Contains(errors, e => e.ErrorMessage == "PER_USER_LIMIT_INVALID");
        }
    }

    [Fact]
    public void MissingDates_ShouldReportBothErrors()
    {
        var dto = ValidCreate();
        dto.StartAt = null;
        dto.EndAt = null;
        ExpectError(dto, "START_AT_REQUIRED", nameof(dto.StartAt));
        ExpectError(dto, "END_AT_REQUIRED", nameof(dto.EndAt));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(9999, false)]
    [InlineData(10000, true)]
    public void DateRange_ShouldValidateAtPersistedPrecision(long ticks, bool valid)
    {
        var dto = ValidCreate();
        dto.EndAt = Start.AddTicks(ticks);
        if (valid) Assert.Empty(Validate(dto));
        else ExpectError(dto, "VOUCHER_DATE_RANGE_INVALID", nameof(dto.EndAt));
    }

    [Fact]
    public void EquivalentInstantsWithDifferentOffsets_ShouldFailDateRange()
    {
        var dto = ValidCreate();
        dto.EndAt = Start.ToOffset(TimeSpan.FromHours(7));
        ExpectError(dto, "VOUCHER_DATE_RANGE_INVALID", nameof(dto.EndAt));
    }

    [Theory]
    [InlineData("2026-12-01T07:00:00.1234567+07:00")]
    [InlineData("2026-12-01T00:00:00.1234567Z")]
    [InlineData("2026-11-30T19:00:00.1234567-05:00")]
    public void JsonDates_WithExplicitOffset_ShouldNormalizeToSameUtc(string raw)
    {
        var dto = DeserializeWithStart(raw);
        Assert.Empty(Validate(dto));
        Assert.NotNull(dto.StartAt);
        var normalized = VoucherInputPolicy.ToUtcMilliseconds(dto.StartAt.GetValueOrDefault());
        Assert.Equal(Start.UtcDateTime.AddMilliseconds(123), normalized);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }

    [Theory]
    [InlineData("2026-12-01T00:00:00")]
    [InlineData("2026-12-01")]
    [InlineData("2026-12-01T00:00:00+99:00")]
    [InlineData("invalid")]
    public void JsonDates_WithoutValidExplicitOffset_ShouldThrow(string raw)
    {
        Assert.Throws<JsonException>(() => DeserializeWithStart(raw));
    }

    [Fact]
    public void JsonDates_MissingOrNull_ShouldFailValidation()
    {
        var dto = JsonSerializer.Deserialize<CreateVoucherDto>(
            """{"code":"SALE","discountType":1,"discountValue":20,"endAt":null}""", WebJson);
        var actual = Assert.IsType<CreateVoucherDto>(dto);
        ExpectError(actual, "START_AT_REQUIRED", nameof(actual.StartAt));
        ExpectError(actual, "END_AT_REQUIRED", nameof(actual.EndAt));
    }

    [Fact]
    public void Update_ShouldShareTermsValidationAndRequireOffset()
    {
        var dto = new UpdateVoucherDto
        {
            DiscountType = DiscountType.Percentage,
            DiscountValue = 101m,
            StartAt = Start,
            EndAt = Start.AddDays(1),
            UsageLimit = 0
        };
        ExpectError(dto, "DISCOUNT_VALUE_INVALID", nameof(dto.DiscountValue));
        ExpectError(dto, "USAGE_LIMIT_INVALID", nameof(dto.UsageLimit));
        dto.DiscountValue = 20m;
        dto.UsageLimit = null;
        Assert.Empty(Validate(dto));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateVoucherDto>(
            """{"startAt":"2026-12-01T00:00:00"}""", WebJson));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Status_ShouldRequireExplicitBoolean(bool? status, bool valid)
    {
        var dto = new UpdateVoucherStatusDto { IsActive = status };
        if (valid) Assert.Empty(Validate(dto));
        else ExpectError(dto, "VOUCHER_STATUS_REQUIRED", nameof(dto.IsActive));
    }

    [Fact]
    public void MultipleInvalidFields_ShouldReportAllErrors()
    {
        var dto = ValidCreate();
        dto.Code = "?";
        dto.DiscountValue = 0;
        dto.UsageLimit = -1;
        dto.EndAt = null;
        var errors = Validate(dto);
        foreach (var code in new[] { "VOUCHER_CODE_INVALID", "DISCOUNT_VALUE_INVALID",
                     "USAGE_LIMIT_INVALID", "END_AT_REQUIRED" })
            Assert.Contains(errors, e => e.ErrorMessage == code);
    }

    [Fact]
    public void RequestContracts_ShouldExposeOnlyAllowedFields()
    {
        string[] terms = ["Description", "DiscountType", "DiscountValue", "MinOrderAmount",
            "MaxDiscountAmount", "UsageLimit", "PerUserLimit", "StartAt", "EndAt"];
        Assert.Equal(terms.OrderBy(x => x), typeof(UpdateVoucherDto).GetProperties().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(terms.Concat(new[] { "Code", "IsActive" }).OrderBy(x => x),
            typeof(CreateVoucherDto).GetProperties().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal("VoucherCode", Assert.Single(typeof(VoucherPreviewRequestDto).GetProperties()).Name);
        Assert.Equal("IsActive", Assert.Single(typeof(UpdateVoucherStatusDto).GetProperties()).Name);
    }

    [Fact]
    public void OptionalCodePolicy_ShouldTreatBlankAsNoVoucher()
    {
        Assert.Null(VoucherInputPolicy.NormalizeCode("  "));
        Assert.Empty(VoucherInputPolicy.ValidateCode("  ", "VoucherCode", required: false));
        Assert.NotEmpty(VoucherInputPolicy.ValidateCode("?", "VoucherCode", required: false));
    }

    private static CreateVoucherDto ValidCreate() => new()
    {
        Code = "FLOWER20",
        DiscountType = DiscountType.Percentage,
        DiscountValue = 20m,
        StartAt = Start,
        EndAt = Start.AddDays(7)
    };

    private static CreateVoucherDto DeserializeWithStart(string raw)
    {
        var json = JsonSerializer.Serialize(new
        {
            code = "FLOWER20", discountType = 1, discountValue = 20m,
            startAt = raw, endAt = "2026-12-08T00:00:00Z"
        });
        return Assert.IsType<CreateVoucherDto>(JsonSerializer.Deserialize<CreateVoucherDto>(json, WebJson));
    }

    private static List<ValidationResult> Validate(object dto)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), errors, validateAllProperties: true);
        return errors;
    }

    private static void ExpectError(object dto, string code, string memberName) =>
        Assert.Contains(Validate(dto), e => e.ErrorMessage == code && e.MemberNames.Contains(memberName));
}
