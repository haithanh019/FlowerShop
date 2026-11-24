using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Domain.Enums;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class CheckoutVoucherFingerprintTests
{
    private static CheckoutRequestDto Request(string? code = null, string? note = null) => new()
    {
        CheckoutRequestId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        ShippingAddressId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
        PaymentMethod = PaymentMethod.CashOnDelivery,
        Note = note,
        VoucherCode = code
    };

    // Golden vectors from the pre-voucher v1 wire format, independently computed.
    [Theory]
    [InlineData(null, "69D81A59F95DF972D87C4183D539D2D5C79B3654ECDA94C9F8DBA74A791EA5D1")]
    [InlineData("", "CA6C0AF48860EFB1732D2C1DA345600D6CACFD8504E923872B382786263AC8AF")]
    [InlineData("Giao hoa buổi sáng 🌹", "10271E3ECBC7D07DD835CAA055A20199BB3EEE860DE7D96AC34E70E56A2B348D")]
    public void LegacyV1_ShouldPreserveGoldenHash(string? note, string expected)
        => Assert.Equal(expected, CheckoutRequestFingerprint.Compute(Request(note: note)));

    [Fact]
    public void LegacyV1_ShouldPreserveLongNoteLengthPrefix()
        => Assert.Equal("8714A7BA8BFA39CC3F11579A8BC6245079BDB757D4B40F1725A6416ECA3612DA",
            CheckoutRequestFingerprint.Compute(Request(note: new string('x', 128))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void NoVoucher_ShouldUseLegacyHash(string? code)
    {
        var dto = Request(code, "Gift");
        Assert.Empty(Validate(dto));
        Assert.Equal(CheckoutRequestFingerprint.Compute(Request(note: "Gift")),
            CheckoutRequestFingerprint.Compute(dto));
    }

    [Theory]
    [InlineData("FLOWER-20")]
    [InlineData("flower-20")]
    [InlineData("  FlOwEr-20  ")]
    public void V2_ShouldNormalizeCodeAndMatchGoldenHash(string code)
    {
        var dto = Request(code);
        Assert.Empty(Validate(dto));
        Assert.Equal("CB3BBCEE01FAA5E0A9BD4CA4B4B6FEFD2A36B7D8B27FD227E699115EB11CEA14",
            CheckoutRequestFingerprint.Compute(dto));
        Assert.Equal(code, dto.VoucherCode); // Hashing must not mutate the request.
    }

    [Fact]
    public void V2_ShouldDistinguishAddingRemovingOrChangingVoucher()
    {
        var hashes = new[] { Request(), Request("FLOWER-20"), Request("FLOWER_20"), Request("FLOWER-21") }
            .Select(CheckoutRequestFingerprint.Compute).ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void V2_ShouldRetainAllOriginalFieldSemantics()
    {
        var original = Request("FLOWER-20", "Gift");
        var hash = CheckoutRequestFingerprint.Compute(original);
        var changedId = Request("FLOWER-20", "Gift"); changedId.CheckoutRequestId = Guid.NewGuid();
        var changedAddress = Request("FLOWER-20", "Gift"); changedAddress.ShippingAddressId = Guid.NewGuid();
        var changedMethod = Request("FLOWER-20", "Gift"); changedMethod.PaymentMethod = PaymentMethod.BankTransfer;
        foreach (var changed in new[] { changedId, changedAddress, changedMethod,
                     Request("FLOWER-20"), Request("FLOWER-20", ""),
                     Request("FLOWER-20", "gift"), Request("FLOWER-20", "Gift ") })
            Assert.NotEqual(hash, CheckoutRequestFingerprint.Compute(changed));
    }

    [Theory]
    [InlineData("FLOWER SALE")]
    [InlineData("FLOWER@20")]
    [InlineData("FLOWER\n20")]
    [InlineData("ＦＬＯＷＥＲ20")]
    [InlineData("flówér20")]
    public void InvalidCode_ShouldFailDtoAndDirectHash(string code)
        => AssertInvalid(code, "VOUCHER_CODE_INVALID");

    [Fact]
    public void CodeLength_ShouldValidateAfterTrim()
    {
        Assert.Empty(Validate(Request("  " + new string('A', 50) + "  ")));
        AssertInvalid(new string('A', 51), "VOUCHER_CODE_TOO_LONG");
    }

    private static void AssertInvalid(string code, string errorCode)
    {
        var dto = Request(code);
        Assert.Contains(Validate(dto), x => x.ErrorMessage == errorCode &&
            x.MemberNames.Contains(nameof(CheckoutRequestDto.VoucherCode)));
        var ex = Assert.Throws<ValidationException>(() => CheckoutRequestFingerprint.Compute(dto));
        Assert.Equal(errorCode, ex.ValidationResult.ErrorMessage);
    }

    private static List<ValidationResult> Validate(CheckoutRequestDto dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }
}
