using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Validation;

namespace FlowerShop.Application.DTOs.Vouchers;

public sealed class VoucherPreviewRequestDto : IValidatableObject
{
    public string VoucherCode { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        VoucherInputPolicy.ValidateCode(VoucherCode, nameof(VoucherCode));
}
