using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class AdminOrderQueryService(
    IAdminOrderReadRepository orders,
    IUserAccessService userAccessService)
    : IAdminOrderQueryService
{
    public async Task<AdminOrderPageDto> GetPageAsync(
        Guid adminUserId,
        OrderStatus? status = null,
        Guid? customerUserId = null,
        string? orderCodeKeyword = null,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate the acting Admin.
        await EnsureAdminAsync(
            adminUserId, cancellationToken);

        // 2. Validate pagination.
        if (page < 1)
        {
            throw new ValidationException(
                "PAGE_MUST_BE_POSITIVE");
        }

        if (pageSize is < 1 or > 50)
        {
            throw new ValidationException(
                "PAGE_SIZE_OUT_OF_RANGE");
        }

        if (((long)page - 1) * pageSize > int.MaxValue)
        {
            throw new ValidationException(
                "PAGE_OUT_OF_RANGE");
        }

        // 3. Validate optional filters.
        if (status.HasValue &&
            !Enum.IsDefined(status.Value))
        {
            throw new ValidationException(
                "INVALID_ORDER_STATUS");
        }

        if (customerUserId == Guid.Empty)
        {
            throw new ValidationException(
                "CUSTOMER_USER_ID_REQUIRED");
        }

        var keyword = orderCodeKeyword?.Trim();

        if (keyword is { Length: > 40 })
        {
            throw new ValidationException(
                "ORDER_CODE_KEYWORD_TOO_LONG");
        }

        // 4. Query orders using the read-only repository.
        var (pageOrders, totalCount) =
            await orders.GetPageAsync(
                status,
                customerUserId,
                keyword,
                page,
                pageSize,
                cancellationToken);

        // 5. Map Domain Entities to Admin DTOs.
        var items = pageOrders
            .Select(ToSummaryDto)
            .ToArray();

        // Avoid overflow from pageSize * TotalPages.
        var totalPages = totalCount == 0
            ? 0
            : 1 + (totalCount - 1) / pageSize;

        return new AdminOrderPageDto(
            items,
            page,
            pageSize,
            totalCount,
            totalPages);
    }

    public async Task<AdminOrderDetailDto> GetDetailAsync(
        Guid adminUserId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(
            adminUserId, cancellationToken);

        if (orderId == Guid.Empty)
        {
            throw new ValidationException(
                "ORDER_ID_REQUIRED");
        }

        var order = await orders.GetByIdAsync(
            orderId,
            cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound,
                "ORDER_NOT_FOUND");
        return new AdminOrderDetailDto(
            order.UserId,
            order.User.Email,
            ToDetailDto(order));
    }

    private async Task EnsureAdminAsync(
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        if (adminUserId == Guid.Empty)
        {
            throw new ValidationException(
                "ADMIN_USER_ID_REQUIRED");
        }

        var isAdmin = await userAccessService
            .IsActiveWithRoleAsync(
                adminUserId,
                nameof(UserRole.Admin),
                cancellationToken);

        if (!isAdmin)
        {
            throw new BusinessException(
                BusinessErrorType.Unauthorized,
                "ADMIN_ACCESS_REQUIRED");
        }
    }

    private static AdminOrderSummaryDto ToSummaryDto(
        Order order)
    {
        // Match the existing Customer Order Query policy:
        // latest Payment = CreatedAt DESC, Id DESC.
        var latestPayment = order.Payments
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();

        return new AdminOrderSummaryDto(
            order.Id,
            order.OrderCode,
            order.UserId,
            order.User.Email,
            order.RecipientName,
            order.Status,
            latestPayment?.Method,
            latestPayment?.Status,
            order.Items.Sum(x => x.Quantity),
            order.TotalAmount,
            order.CreatedAt);
    }

    private static OrderDetailDto ToDetailDto(
        Order order)
    {
        // Use stored snapshots, not current Product prices.
        var items = order.Items
            .OrderBy(x => x.ProductId)
            .Select(x => new OrderItemDto(
                x.ProductId,
                x.ProductName,
                x.UnitPrice,
                x.Quantity,
                x.LineTotal))
            .ToArray();

        // Include complete Payment history in a stable order.
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
}
