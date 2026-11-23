using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class OrderQueryService(IOrderReadRepository orders)
    : IOrderQueryService
{
    public async Task<OrderHistoryDto> GetHistoryAsync(
        Guid userId,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        if (page < 1)
            throw new ValidationException("PAGE_MUST_BE_POSITIVE");

        if (pageSize is < 1 or > 50)
            throw new ValidationException("PAGE_SIZE_OUT_OF_RANGE");

        if (((long)page - 1) * pageSize > int.MaxValue)
            throw new ValidationException("PAGE_OUT_OF_RANGE");

        var (pageOrders, totalCount) = await orders
            .GetPageByUserIdAsync(userId, page, pageSize, cancellationToken);

        var items = pageOrders.Select(order =>
        {
            var latestPayment = order.Payments
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();

            return new OrderSummaryDto(
                order.Id,
                order.OrderCode,
                order.Status,
                latestPayment?.Method,
                latestPayment?.Status,
                order.Items.Sum(x => x.Quantity),
                order.TotalAmount,
                order.CreatedAt);
        }).ToArray();

        var totalPages = totalCount == 0
            ? 0
            : 1 + (totalCount - 1) / pageSize;

        return new OrderHistoryDto(
            items, page, pageSize, totalCount, totalPages);
    }

    public async Task<OrderDetailDto> GetDetailAsync(
        Guid userId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        if (orderId == Guid.Empty)
            throw new ValidationException("ORDER_ID_REQUIRED");

        var order = await orders.GetByIdAndUserIdAsync(
            orderId, userId, cancellationToken);

        if (order is null)
            throw new BusinessException(
                BusinessErrorType.NotFound, "ORDER_NOT_FOUND");

        return ToDetailDto(order);
    }

    private static OrderDetailDto ToDetailDto(Order order)
    {
        var items = order.Items
            .OrderBy(x => x.ProductId)
            .Select(x => new OrderItemDto(
                x.ProductId,
                x.ProductName,
                x.UnitPrice,
                x.Quantity,
                x.LineTotal))
            .ToArray();

        var payments = order.Payments
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new OrderPaymentDto(
                x.Id,
                x.Method,
                x.Status,
                x.Amount,
                x.Currency,
                x.PaidAt,
                x.CreatedAt))
            .ToArray();

        return new OrderDetailDto(
            order.Id,
            order.OrderCode,
            order.Status,
            order.RecipientName,
            order.RecipientPhone,
            order.ShippingAddressLine,
            order.ShippingWard,
            order.ShippingDistrict,
            order.ShippingProvince,
            order.ShippingCountryCode,
            order.ShippingPostalCode,
            order.Note,
            order.Subtotal,
            order.ShippingFee,
            order.DiscountAmount,
            order.TotalAmount,
            items,
            payments,
            order.CreatedAt);
    }

    private static void ValidateUserId(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");
    }
}
