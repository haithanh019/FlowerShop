using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed class CheckoutRequestDto : IValidatableObject
{
    public Guid CheckoutRequestId { get; set; }
    public Guid ShippingAddressId { get; set; }
    public PaymentMethod PaymentMethod { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }

    // Optional. Blank means no voucher; validate ASCII before normalization.
    public string? VoucherCode { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CheckoutRequestId == Guid.Empty)
            yield return new ValidationResult("CHECKOUT_REQUEST_ID_REQUIRED", [nameof(CheckoutRequestId)]);
        if (ShippingAddressId == Guid.Empty)
            yield return new ValidationResult("SHIPPING_ADDRESS_ID_REQUIRED", [nameof(ShippingAddressId)]);
        if (PaymentMethod != FlowerShop.Domain.Enums.PaymentMethod.CashOnDelivery)
            yield return new ValidationResult("PAYMENT_METHOD_NOT_SUPPORTED", [nameof(PaymentMethod)]);

        foreach (var error in VoucherInputPolicy.ValidateCode(VoucherCode, nameof(VoucherCode), required: false))
            yield return error;
    }
}
