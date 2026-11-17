using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Policies;

public static class OrderStatusTransitionPolicy
{
    public static bool CanTransition(
        OrderStatus current,
        OrderStatus next,
        UserRole actor)
    {
        if (current == next)
            return false;

        return actor switch
        {
            UserRole.Customer =>
                current == OrderStatus.Pending &&
                next == OrderStatus.Cancelled,

            UserRole.Admin => (current, next) switch
            {
                (OrderStatus.Pending,
                 OrderStatus.Confirmed) => true,

                (OrderStatus.Confirmed,
                 OrderStatus.Preparing) => true,

                (OrderStatus.Preparing,
                 OrderStatus.Shipping) => true,

                (OrderStatus.Shipping,
                 OrderStatus.Delivered) => true,

                (OrderStatus.Pending,
                 OrderStatus.Cancelled) => true,

                (OrderStatus.Confirmed,
                 OrderStatus.Cancelled) => true,

                _ => false
            },

            _ => false
        };
    }

    public static bool RequiresStockRestoration(
        OrderStatus current,
        OrderStatus next,
        UserRole actor)
    {
        return next == OrderStatus.Cancelled &&
               CanTransition(current, next, actor);
    }

    public static bool IsTerminal(OrderStatus status)
    {
        return status is
            OrderStatus.Delivered or
            OrderStatus.Cancelled;
    }
}
