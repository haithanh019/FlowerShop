using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class VoucherPricingPolicyTests
{
    private static readonly DateTime Now = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Percentage_ShouldApplyCapAndExcludeShippingFromDiscountBase()
    {
        var voucher = NewVoucher();
        voucher.MaxDiscountAmount = 80000m;
        var price = Evaluate(voucher, 500000m, 30000m);
        Assert.Equal(new VoucherPrice(500000m, 30000m, 80000m, 450000m), price);
        voucher.MaxDiscountAmount = null;
        Assert.Equal(100000m, Evaluate(voucher, 500000m, 30000m).DiscountAmount);
    }

    [Fact]
    public void Percentage_ShouldRoundMidpointAwayFromZero()
    {
        var voucher = NewVoucher();
        voucher.DiscountValue = 10m;
        var price = Evaluate(voucher, 10.05m);
        Assert.Equal(1.01m, price.DiscountAmount);
        Assert.Equal(9.04m, price.TotalAmount);
    }

    [Fact]
    public void FixedAmount_ShouldClampToSubtotalWithoutDiscountingShipping()
    {
        var voucher = NewVoucher();
        voucher.DiscountType = DiscountType.FixedAmount;
        voucher.DiscountValue = 50000m;
        Assert.Equal(50000m, Evaluate(voucher, 100000m).DiscountAmount);
        Assert.Equal(new VoucherPrice(30000m, 20000m, 30000m, 20000m),
            Evaluate(voucher, 30000m, 20000m));
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => Evaluate(voucher, 30000m));
    }

    [Fact]
    public void Eligibility_ShouldUseInclusiveStartAndExclusiveEnd()
    {
        var voucher = NewVoucher();
        voucher.StartAt = Now;
        Assert.Equal(20m, Evaluate(voucher, 100m).DiscountAmount);
        Expect("VOUCHER_NOT_STARTED", () => Evaluate(voucher, 100m, now: Now.AddMilliseconds(-1)));
        Assert.Equal(20m, Evaluate(voucher, 100m, now: voucher.EndAt.AddMilliseconds(-1)).DiscountAmount);
        Expect("VOUCHER_EXPIRED", () => Evaluate(voucher, 100m, now: voucher.EndAt));
        voucher.IsActive = false;
        Expect("VOUCHER_INACTIVE", () => Evaluate(voucher, 100m, now: voucher.EndAt));
    }

    [Fact]
    public void Eligibility_ShouldUseSubtotalNotShippingForMinimumSpend()
    {
        var voucher = NewVoucher();
        voucher.MinOrderAmount = 100000m;
        Expect("VOUCHER_MIN_ORDER_NOT_MET", () => Evaluate(voucher, 99999.99m, 10000m));
        Assert.Equal(20000m, Evaluate(voucher, 100000m).DiscountAmount);
    }

    [Fact]
    public void Eligibility_ShouldEnforceGlobalAndCustomerLimits()
    {
        var voucher = NewVoucher();
        voucher.UsedCount = 4;
        voucher.UsageLimit = 5;
        voucher.PerUserLimit = 2;
        Assert.Equal(20m, Evaluate(voucher, 100m, used: 4, customer: 1).DiscountAmount);
        Expect("VOUCHER_PER_USER_LIMIT_REACHED", () => Evaluate(voucher, 100m, used: 4, customer: 2));
        voucher.UsedCount = 5;
        Expect("VOUCHER_USAGE_LIMIT_REACHED", () => Evaluate(voucher, 100m, used: 5));
        voucher.UsageLimit = null;
        voucher.PerUserLimit = null;
        Assert.Equal(20m, Evaluate(voucher, 100m, used: 5, customer: 5).DiscountAmount);
        voucher.UsedCount = int.MaxValue;
        Expect("VOUCHER_USAGE_LIMIT_REACHED", () => Evaluate(voucher, 100m, used: int.MaxValue));
    }

    [Fact]
    public void Eligibility_ShouldFailClosedOnInconsistentUsage()
    {
        var voucher = NewVoucher();
        voucher.UsedCount = 2;
        Expect("VOUCHER_USAGE_INCONSISTENT", () => Evaluate(voucher, 100m, used: 1));
        Expect("VOUCHER_USAGE_INCONSISTENT", () => Evaluate(voucher, 100m, used: 2, customer: 3));
        Expect("VOUCHER_USAGE_INCONSISTENT", () => Evaluate(voucher, 100m, used: 2, customer: -1));
        voucher.UsageLimit = 1;
        Expect("VOUCHER_USAGE_INCONSISTENT", () => Evaluate(voucher, 100m, used: 2));
    }

    [Fact]
    public void Calculation_ShouldRejectZeroDiscountAndZeroPayableCod()
    {
        var voucher = NewVoucher();
        voucher.DiscountValue = 0.01m;
        Expect("VOUCHER_DISCOUNT_ZERO", () => Evaluate(voucher, 0.01m));
        voucher.DiscountValue = 100m;
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => Evaluate(voucher, 100m));
        voucher.MaxDiscountAmount = 90m;
        Assert.Equal(10m, Evaluate(voucher, 100m).TotalAmount);
    }

    [Fact]
    public void Calculation_ShouldHandleSqlMoneyBoundaries()
    {
        var voucher = NewVoucher();
        voucher.DiscountValue = 50m;
        var max = VoucherInputPolicy.MaxStoredAmount;
        var price = Evaluate(voucher, max);
        Assert.Equal(5_000_000_000_000_000m, price.DiscountAmount);
        Assert.Equal(4_999_999_999_999_999.99m, price.TotalAmount);
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => Evaluate(voucher, max + 0.01m));
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => Evaluate(voucher, 10.001m));
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => Evaluate(voucher, 100m, -1m));
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => Evaluate(voucher, max, max));
    }

    [Fact]
    public void Eligibility_ShouldRejectInvalidStoredTerms()
    {
        var voucher = NewVoucher();
        voucher.DiscountType = (DiscountType)0;
        Expect("VOUCHER_CONFIGURATION_INVALID", () => Evaluate(voucher, 100m));
        voucher.DiscountType = DiscountType.Percentage;
        voucher.DiscountValue = 100.01m;
        Expect("VOUCHER_CONFIGURATION_INVALID", () => Evaluate(voucher, 100m));
        voucher.DiscountValue = 20.001m;
        Expect("VOUCHER_CONFIGURATION_INVALID", () => Evaluate(voucher, 100m));
        voucher.DiscountType = DiscountType.FixedAmount;
        voucher.DiscountValue = 20m;
        voucher.MaxDiscountAmount = 10m;
        Expect("VOUCHER_CONFIGURATION_INVALID", () => Evaluate(voucher, 100m));
    }

    [Fact]
    public void Subtotal_ShouldValidateEveryLineAndRejectOverflow()
    {
        Assert.Equal(500000m, CheckoutPricingPolicy.CalculateSubtotal([(150000m, 2), (200000m, 1)]));
        var max = VoucherInputPolicy.MaxStoredAmount;
        Assert.Equal(max, CheckoutPricingPolicy.CalculateSubtotal([(max, 1)]));
        Expect("CART_EMPTY", () => CheckoutPricingPolicy.CalculateSubtotal([]));
        Expect("CART_INVALID_QUANTITY", () => CheckoutPricingPolicy.CalculateSubtotal([(10m, 0)]));
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => CheckoutPricingPolicy.CalculateSubtotal([(0m, 1)]));
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => CheckoutPricingPolicy.CalculateSubtotal([(max, 2)]));
        Expect("CHECKOUT_TOTAL_OUT_OF_RANGE", () => CheckoutPricingPolicy.CalculateSubtotal([(max, 1), (0.01m, 1)]));
    }

    [Fact]
    public void Evaluate_ShouldNotMutateVoucherAndRequireUtcClock()
    {
        var voucher = NewVoucher();
        var timestamp = voucher.UpdatedAt;
        var first = Evaluate(voucher, 100m);
        Assert.Equal(first, Evaluate(voucher, 100m));
        Assert.Equal(0, voucher.UsedCount);
        Assert.Equal(timestamp, voucher.UpdatedAt);
        Assert.Throws<ArgumentException>(() => Evaluate(voucher, 100m,
            now: DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
    }

    private static Voucher NewVoucher() => new()
    {
        Code = "TEST20", DiscountType = DiscountType.Percentage, DiscountValue = 20m,
        StartAt = Now.AddDays(-1), EndAt = Now.AddDays(1), IsActive = true
    };

    private static VoucherPrice Evaluate(Voucher voucher, decimal subtotal,
        decimal shipping = 0m, long used = 0, long customer = 0, DateTime? now = null)
        => VoucherPricingPolicy.Evaluate(voucher, subtotal, shipping, used, customer, now ?? Now);

    private static void Expect(string code, Action action)
    {
        var ex = Assert.Throws<BusinessException>(action);
        Assert.Equal(BusinessErrorType.Conflict, ex.ErrorType);
        Assert.Equal(code, ex.Message);
    }
}
