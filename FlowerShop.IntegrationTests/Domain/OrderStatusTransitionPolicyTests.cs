using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Policies;
using Xunit;

namespace FlowerShop.IntegrationTests.Domain;

public sealed class OrderStatusTransitionPolicyTests
{
    [Theory]
    [InlineData(
        OrderStatus.Pending,
        OrderStatus.Confirmed)]
    [InlineData(
        OrderStatus.Confirmed,
        OrderStatus.Preparing)]
    [InlineData(
        OrderStatus.Preparing,
        OrderStatus.Shipping)]
    [InlineData(
        OrderStatus.Shipping,
        OrderStatus.Delivered)]
    [InlineData(
        OrderStatus.Pending,
        OrderStatus.Cancelled)]
    [InlineData(
        OrderStatus.Confirmed,
        OrderStatus.Cancelled)]
    public void Admin_ShouldAllowValidTransitions(
        OrderStatus current,
        OrderStatus next)
    {
        var allowed =
            OrderStatusTransitionPolicy.CanTransition(
                current,
                next,
                UserRole.Admin);

        Assert.True(allowed);
    }


    [Theory]
    [InlineData(
        OrderStatus.Confirmed,
        OrderStatus.Pending)]
    [InlineData(
        OrderStatus.Pending,
        OrderStatus.Shipping)]
    [InlineData(
        OrderStatus.Confirmed,
        OrderStatus.Delivered)]
    [InlineData(
        OrderStatus.Preparing,
        OrderStatus.Cancelled)]
    [InlineData(
        OrderStatus.Shipping,
        OrderStatus.Cancelled)]
    [InlineData(
        OrderStatus.Delivered,
        OrderStatus.Cancelled)]
    [InlineData(
        OrderStatus.Cancelled,
        OrderStatus.Pending)]
    [InlineData(
        OrderStatus.Cancelled,
        OrderStatus.Cancelled)]
    public void Admin_ShouldRejectInvalidTransitions(
        OrderStatus current,
        OrderStatus next)
    {
        Assert.False(
            OrderStatusTransitionPolicy.CanTransition(
                current,
                next,
                UserRole.Admin));
    }


    [Fact]
    public void Customer_CanCancelPendingOrder()
    {
        Assert.True(
            OrderStatusTransitionPolicy.CanTransition(
                OrderStatus.Pending,
                OrderStatus.Cancelled,
                UserRole.Customer));
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Preparing)]
    [InlineData(OrderStatus.Shipping)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void Customer_CannotCancelOtherStatuses(
        OrderStatus current)
    {
        Assert.False(
            OrderStatusTransitionPolicy.CanTransition(
                current,
                OrderStatus.Cancelled,
                UserRole.Customer));
    }

    [Fact]
    public void Customer_CannotConfirmOrder()
    {
        Assert.False(
            OrderStatusTransitionPolicy.CanTransition(
                OrderStatus.Pending,
                OrderStatus.Confirmed,
                UserRole.Customer));
    }

    [Theory]
    [InlineData(OrderStatus.Pending, UserRole.Customer)]
    [InlineData(OrderStatus.Pending, UserRole.Admin)]
    [InlineData(OrderStatus.Confirmed, UserRole.Admin)]
    public void ValidCancellation_RequiresRestocking(
        OrderStatus current,
        UserRole actor)
    {
        Assert.True(
            OrderStatusTransitionPolicy
                .RequiresStockRestoration(
                    current,
                    OrderStatus.Cancelled,
                    actor));
    }

    [Fact]
    public void RepeatedCancellation_MustNotRestock()
    {
        Assert.False(
            OrderStatusTransitionPolicy
                .RequiresStockRestoration(
                    OrderStatus.Cancelled,
                    OrderStatus.Cancelled,
                    UserRole.Admin));
    }

    [Fact]
    public void NormalProgression_MustNotRestock()
    {
        Assert.False(
            OrderStatusTransitionPolicy
                .RequiresStockRestoration(
                    OrderStatus.Pending,
                    OrderStatus.Confirmed,
                    UserRole.Admin));
    }

    [Theory]
    [InlineData(OrderStatus.Delivered, true)]
    [InlineData(OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Pending, false)]
    [InlineData(OrderStatus.Confirmed, false)]
    [InlineData(OrderStatus.Preparing, false)]
    [InlineData(OrderStatus.Shipping, false)]
    public void TerminalStatus_ShouldBeIdentified(
        OrderStatus status,
        bool expected)
    {
        Assert.Equal(
            expected,
            OrderStatusTransitionPolicy.IsTerminal(status));
    }

    [Fact]
    public void UnknownRole_MustNotChangeOrder()
    {
        Assert.False(
            OrderStatusTransitionPolicy.CanTransition(
                OrderStatus.Pending,
                OrderStatus.Confirmed,
                (UserRole)999));
    }
}
