using FlowerShop.Application.Common.Validation;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class OrderFinancialConsistencyTests
{
    [Fact]
    public void ValidCodPayment_RequiresExactlyOneMatchingPayment()
    {
        var order = new Order
        {
            Subtotal = 320000m,
            TotalAmount = 320000m
        };
        var payment = new Payment
        {
            OrderId = order.Id,
            Method = PaymentMethod.CashOnDelivery,
            Status = PaymentStatus.Pending,
            Amount = 320000m,
            Currency = "VND"
        };

        Assert.True(OrderFinancialConsistency.HasValidCodPayment(
            order, [payment]));
        Assert.False(OrderFinancialConsistency.HasValidCodPayment(
            order, []));
        Assert.False(OrderFinancialConsistency.HasValidCodPayment(
            order, [payment, payment]));

        payment.Amount = 120000m;
        Assert.False(OrderFinancialConsistency.HasValidCodPayment(
            order, [payment]));
        payment.Amount = order.TotalAmount;

        payment.Currency = "USD";
        Assert.False(OrderFinancialConsistency.HasValidCodPayment(
            order, [payment]));
        payment.Currency = "VND";

        payment.OrderId = Guid.NewGuid();
        Assert.False(OrderFinancialConsistency.HasValidCodPayment(
            order, [payment]));
        payment.OrderId = order.Id;

        payment.Method = PaymentMethod.BankTransfer;
        Assert.False(OrderFinancialConsistency.HasValidCodPayment(
            order, [payment]));
    }

    [Fact]
    public void ValidOrderItems_RequiresUniquePositiveLinesAndCorrectSubtotal()
    {
        var order = new Order
        {
            Subtotal = 320000m,
            TotalAmount = 320000m
        };
        var rose = new OrderItem
        {
            OrderId = order.Id,
            ProductId = Guid.NewGuid(),
            ProductName = "Rose",
            UnitPrice = 120000m,
            Quantity = 2
        };
        var tulip = new OrderItem
        {
            OrderId = order.Id,
            ProductId = Guid.NewGuid(),
            ProductName = "Tulip",
            UnitPrice = 80000m,
            Quantity = 1
        };
        Assert.True(OrderFinancialConsistency.HasValidOrderItems(
            order, [rose, tulip]));
        Assert.False(OrderFinancialConsistency.HasValidOrderItems(
            order, []));
        Assert.False(OrderFinancialConsistency.HasValidOrderItems(
            order, [rose]));
        Assert.False(OrderFinancialConsistency.HasValidOrderItems(
            order, [rose, rose]));

        tulip.Quantity = 0;
        Assert.False(OrderFinancialConsistency.HasValidOrderItems(
            order, [rose, tulip]));
        tulip.Quantity = 1;

        tulip.OrderId = Guid.NewGuid();
        Assert.False(OrderFinancialConsistency.HasValidOrderItems(
            order, [rose, tulip]));
        tulip.OrderId = order.Id;

        tulip.UnitPrice = decimal.MaxValue;
        Assert.False(OrderFinancialConsistency.HasValidOrderItems(
            order, [rose, tulip]));
    }
}
