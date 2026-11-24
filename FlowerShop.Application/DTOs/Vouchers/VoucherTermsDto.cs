using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FlowerShop.Application.Common.Serialization;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Vouchers;

public abstract class VoucherTermsDto : IValidatableObject
{
    public string? Description { get; set; }
    public DiscountType DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public decimal MinOrderAmount { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public int? UsageLimit { get; set; }
    public int? PerUserLimit { get; set; }

    [JsonConverter(typeof(ExplicitOffsetDateTimeConverter))]
    public DateTimeOffset? StartAt { get; set; }

    [JsonConverter(typeof(ExplicitOffsetDateTimeConverter))]
    public DateTimeOffset? EndAt { get; set; }

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (VoucherInputPolicy.NormalizeDescription(Description) is { Length: > 500 })
            yield return new ValidationResult("DESCRIPTION_TOO_LONG", [nameof(Description)]);

        if (!Enum.IsDefined(DiscountType))
            yield return new ValidationResult("DISCOUNT_TYPE_INVALID", [nameof(DiscountType)]);

        if (DiscountValue <= 0m || DiscountValue > VoucherInputPolicy.MaxStoredAmount ||
            (DiscountType == FlowerShop.Domain.Enums.DiscountType.Percentage && DiscountValue > 100m))
            yield return new ValidationResult("DISCOUNT_VALUE_INVALID", [nameof(DiscountValue)]);
        else if (!VoucherInputPolicy.HasMoneyPrecision(DiscountValue))
            yield return new ValidationResult("DISCOUNT_VALUE_PRECISION_INVALID", [nameof(DiscountValue)]);

        if (MinOrderAmount < 0m || MinOrderAmount > VoucherInputPolicy.MaxStoredAmount)
            yield return new ValidationResult("MIN_ORDER_AMOUNT_INVALID", [nameof(MinOrderAmount)]);
        else if (!VoucherInputPolicy.HasMoneyPrecision(MinOrderAmount))
            yield return new ValidationResult("MIN_ORDER_AMOUNT_PRECISION_INVALID", [nameof(MinOrderAmount)]);

        if (MaxDiscountAmount is { } cap)
        {
            if (DiscountType == FlowerShop.Domain.Enums.DiscountType.FixedAmount)
                yield return new ValidationResult("MAX_DISCOUNT_NOT_ALLOWED_FOR_FIXED", [nameof(MaxDiscountAmount)]);

            if (cap <= 0m || cap > VoucherInputPolicy.MaxStoredAmount)
                yield return new ValidationResult("MAX_DISCOUNT_AMOUNT_INVALID", [nameof(MaxDiscountAmount)]);
            else if (!VoucherInputPolicy.HasMoneyPrecision(cap))
                yield return new ValidationResult("MAX_DISCOUNT_AMOUNT_PRECISION_INVALID", [nameof(MaxDiscountAmount)]);
        }

        if (UsageLimit is <= 0)
            yield return new ValidationResult("USAGE_LIMIT_INVALID", [nameof(UsageLimit)]);

        if (PerUserLimit is <= 0)
            yield return new ValidationResult("PER_USER_LIMIT_INVALID", [nameof(PerUserLimit)]);

        if (StartAt is null)
            yield return new ValidationResult("START_AT_REQUIRED", [nameof(StartAt)]);

        if (EndAt is null)
            yield return new ValidationResult("END_AT_REQUIRED", [nameof(EndAt)]);

        if (StartAt is { } start && EndAt is { } end &&
            VoucherInputPolicy.ToUtcMilliseconds(start) >= VoucherInputPolicy.ToUtcMilliseconds(end))
            yield return new ValidationResult("VOUCHER_DATE_RANGE_INVALID", [nameof(StartAt), nameof(EndAt)]);
    }
}
