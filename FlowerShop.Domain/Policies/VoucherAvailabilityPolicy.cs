using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using System.Linq.Expressions;

namespace FlowerShop.Domain.Policies;

public static class VoucherAvailabilityPolicy
{
    // A single expression serves both SQL filtering and in-memory classification.
    // All comparisons use a single, caller-supplied UTC timestamp.
    public static Expression<Func<Voucher, VoucherAvailabilityStatus>>
        StatusExpression(DateTime nowUtc) => voucher =>
            !voucher.IsActive ? VoucherAvailabilityStatus.Inactive :
            nowUtc < voucher.StartAt ? VoucherAvailabilityStatus.Upcoming :
            nowUtc >= voucher.EndAt ? VoucherAvailabilityStatus.Expired :
            voucher.UsedCount == int.MaxValue ||
            (voucher.UsageLimit.HasValue &&
             voucher.UsedCount >= voucher.UsageLimit.Value)
                ? VoucherAvailabilityStatus.Exhausted
                : VoucherAvailabilityStatus.Available;

    // Compose without Expression.Invoke (which may not translate to SQL).
    public static Expression<Func<Voucher, bool>> MatchesStatus(
        VoucherAvailabilityStatus status, DateTime nowUtc)
    {
        var expression = StatusExpression(nowUtc);
        return Expression.Lambda<Func<Voucher, bool>>(
            Expression.Equal(expression.Body, Expression.Constant(status)),
            expression.Parameters);
    }
}
