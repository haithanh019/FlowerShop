using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Policies;
using Xunit;

namespace FlowerShop.IntegrationTests.Domain;

public sealed class CodPaymentLifecyclePolicyTests
{
    [Fact]
    public void Policy_ShouldEnforceCodSettlementLifecycle()
    {
        var paidAt = DateTime.UtcNow;

        foreach (var orderStatus in Enum.GetValues<OrderStatus>())
            foreach (var paymentMethod in Enum.GetValues<PaymentMethod>())
                foreach (var paymentStatus in Enum.GetValues<PaymentStatus>())
                    foreach (var paidAtUtc in new DateTime?[] { null, paidAt })
                    {
                        // Expected permission to record COD collection.
                        var canCollect =
                            orderStatus == OrderStatus.Shipping &&
                            paymentMethod == PaymentMethod.CashOnDelivery &&
                            paymentStatus == PaymentStatus.Pending &&
                            paidAtUtc is null;

                        Assert.Equal(
                            canCollect,
                            CodPaymentLifecyclePolicy.CanConfirmCollection(
                                orderStatus,
                                paymentMethod,
                                paymentStatus,
                                paidAtUtc));

                        // Expected permission to complete delivery.
                        var canDeliver =
                            orderStatus == OrderStatus.Shipping &&
                            paymentMethod == PaymentMethod.CashOnDelivery &&
                            paymentStatus == PaymentStatus.Paid &&
                            paidAtUtc.HasValue;

                        Assert.Equal(
                            canDeliver,
                            CodPaymentLifecyclePolicy.CanMarkDelivered(
                                orderStatus,
                                paymentMethod,
                                paymentStatus,
                                paidAtUtc));
                    }
    }
}
