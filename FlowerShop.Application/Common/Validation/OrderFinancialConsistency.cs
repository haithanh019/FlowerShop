using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.Common.Validation;

/// <summary>
/// Validates persisted COD financial snapshots before state-changing actions.
/// The status-specific permissions are intentionally enforced by the caller.
/// </summary>
public static class OrderFinancialConsistency
{
    public static bool HasValidCodPayment(
        Order order,
        IReadOnlyList<Payment> payments)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(payments);

        if (payments.Count != 1)
            return false;

        var payment = payments[0];
        return payment.OrderId == order.Id &&
               payment.Method == PaymentMethod.CashOnDelivery &&
               payment.Currency == "VND" &&
               order.TotalAmount > 0m &&
               payment.Amount == order.TotalAmount;
    }

    public static bool HasValidOrderItems(
        Order order,
        IReadOnlyList<OrderItem> items)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0 || order.Subtotal <= 0m)
            return false;

        var productIds = new HashSet<Guid>();
        decimal actualSubtotal = 0m;

        try
        {
            foreach (var item in items)
            {
                if (item.OrderId != order.Id ||
                    item.ProductId == Guid.Empty ||
                    !productIds.Add(item.ProductId) ||
                    item.Quantity <= 0 ||
                    item.UnitPrice <= 0m)
                    return false;

                actualSubtotal = checked(actualSubtotal +
                    checked(item.UnitPrice * item.Quantity));
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return actualSubtotal == order.Subtotal;
    }
}
