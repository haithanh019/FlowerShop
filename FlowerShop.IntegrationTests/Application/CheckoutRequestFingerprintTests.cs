using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Domain.Enums;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class CheckoutRequestFingerprintTests
{
    [Fact]
    public void Compute_SamePayload_ShouldReturnStableUppercaseSha256()
    {
        var checkoutId = Guid.NewGuid();
        var addressId = Guid.NewGuid();

        var first = Create(checkoutId, addressId, "Giao hoa buổi sáng 🌹");
        var second = Create(checkoutId, addressId, "Giao hoa buổi sáng 🌹");

        var firstHash = CheckoutRequestFingerprint.Compute(first);
        var secondHash = CheckoutRequestFingerprint.Compute(second);

        Assert.Equal(firstHash, secondHash);
        Assert.Matches("^[0-9A-F]{64}$", firstHash);
    }

    [Fact]
    public void Compute_ChangesToAnyPayloadField_ShouldChangeFingerprint()
    {
        var checkoutId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        const string note = "Deliver before noon";
        var original = Create(checkoutId, addressId, note);
        var baseline = CheckoutRequestFingerprint.Compute(original);

        Assert.NotEqual(baseline, CheckoutRequestFingerprint.Compute(
            Create(Guid.NewGuid(), addressId, note)));

        Assert.NotEqual(baseline, CheckoutRequestFingerprint.Compute(
            Create(checkoutId, Guid.NewGuid(), note)));

        Assert.NotEqual(baseline, CheckoutRequestFingerprint.Compute(
            Create(checkoutId, addressId, "Deliver after noon")));

        // Hashing is independent of DTO validation. This covers the
        // PaymentMethod field, even though checkout currently accepts COD only.
        var otherMethod = Create(checkoutId, addressId, note);
        otherMethod.PaymentMethod = PaymentMethod.BankTransfer;
        Assert.NotEqual(baseline,
            CheckoutRequestFingerprint.Compute(otherMethod));
    }

    [Fact]
    public void Compute_NullEmptyWhitespaceAndCase_ShouldRemainDifferent()
    {
        var checkoutId = Guid.NewGuid();
        var addressId = Guid.NewGuid();

        var hashes = new[]
        {
            CheckoutRequestFingerprint.Compute(Create(checkoutId, addressId, null)),
            CheckoutRequestFingerprint.Compute(Create(checkoutId, addressId, "")),
            CheckoutRequestFingerprint.Compute(Create(checkoutId, addressId, " ")),
            CheckoutRequestFingerprint.Compute(Create(checkoutId, addressId, "Gift")),
            CheckoutRequestFingerprint.Compute(Create(checkoutId, addressId, "gift"))
        };

        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void Compute_NullDto_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CheckoutRequestFingerprint.Compute(null!));
    }

    private static CheckoutRequestDto Create(
        Guid checkoutId, Guid addressId, string? note) => new()
    {
        CheckoutRequestId = checkoutId,
        ShippingAddressId = addressId,
        PaymentMethod = PaymentMethod.CashOnDelivery,
        Note = note
    };
}
