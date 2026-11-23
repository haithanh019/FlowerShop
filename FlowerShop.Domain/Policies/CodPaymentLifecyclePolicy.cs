using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Policies;

public static class CodPaymentLifecyclePolicy
{
    // COD collection can be confirmed only while
    // the order is shipping and payment is pending.
    public static bool CanConfirmCollection(
        OrderStatus orderStatus,
        PaymentMethod paymentMethod,
        PaymentStatus paymentStatus,
        DateTime? paidAtUtc)
    {
        return orderStatus == OrderStatus.Shipping &&
               paymentMethod == PaymentMethod.CashOnDelivery &&
               paymentStatus == PaymentStatus.Pending &&
               paidAtUtc is null;
    }

    // Delivery completion requires verified COD settlement.
    public static bool CanMarkDelivered(
        OrderStatus orderStatus,
        PaymentMethod paymentMethod,
        PaymentStatus paymentStatus,
        DateTime? paidAtUtc)
    {
        return orderStatus == OrderStatus.Shipping &&
               paymentMethod == PaymentMethod.CashOnDelivery &&
               paymentStatus == PaymentStatus.Paid &&
               paidAtUtc.HasValue;
    }
}
