using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace FlowerShop.Application.Common.Validation;

public static partial class VoucherInputPolicy
{
    public const decimal MaxStoredAmount = 9_999_999_999_999_999.99m;

    // Call after validation. Blank means no code (for optional checkout input).
    public static string? NormalizeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    public static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    public static IEnumerable<ValidationResult> ValidateCode(
        string? code, string memberName, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            if (required)
                yield return new ValidationResult("VOUCHER_CODE_REQUIRED", [memberName]);
            yield break;
        }

        var trimmed = code.Trim();
        if (trimmed.Length > 50)
            yield return new ValidationResult("VOUCHER_CODE_TOO_LONG", [memberName]);
        else if (!CodeRegex().IsMatch(trimmed))
            yield return new ValidationResult("VOUCHER_CODE_INVALID", [memberName]);
    }

    // Numeric representability, not the textual number of trailing zeros.
    public static bool HasMoneyPrecision(decimal value) =>
        value == decimal.Round(value, 2);

    // Truncate to the exact precision of SQL datetime2(3).
    public static DateTime ToUtcMilliseconds(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    // Validate raw ASCII before uppercase conversion to reject Unicode lookalikes.
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,50}\z")]
    private static partial Regex CodeRegex();
}
