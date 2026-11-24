using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Validation;

namespace FlowerShop.Application.DTOs.Vouchers;

public sealed class CreateVoucherDto : VoucherTermsDto
{
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var error in VoucherInputPolicy.ValidateCode(Code, nameof(Code)))
            yield return error;

        foreach (var error in base.Validate(validationContext))
            yield return error;
    }
}
