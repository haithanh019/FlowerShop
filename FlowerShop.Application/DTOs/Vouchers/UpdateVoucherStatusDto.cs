using System.ComponentModel.DataAnnotations;

namespace FlowerShop.Application.DTOs.Vouchers;

public sealed class UpdateVoucherStatusDto : IValidatableObject
{
    // Distinguish omitted/null from explicit false.
    public bool? IsActive { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IsActive is null)
            yield return new ValidationResult("VOUCHER_STATUS_REQUIRED", [nameof(IsActive)]);
    }
}
