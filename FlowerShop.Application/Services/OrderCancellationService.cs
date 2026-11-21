using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Domain.Policies;
using FlowerShop.Application.Common.Validation;

namespace FlowerShop.Application.Services;

public sealed class OrderCancellationService(
    IUnitOfWork unitOfWork,
    IProductStockRepository stockRepository)
    : IOrderCancellationService
{
    public Task<OrderStatusUpdateResultDto> CancelByCustomerAsync(
        Guid customerUserId,
        Guid orderId,
        CancellationToken cancellationToken = default)
        => CancelAsync(customerUserId, orderId, UserRole.Customer, cancellationToken);

    public Task<OrderStatusUpdateResultDto> CancelByAdminAsync(
        Guid adminUserId,
        Guid orderId,
        CancellationToken cancellationToken = default)
        => CancelAsync(adminUserId, orderId, UserRole.Admin, cancellationToken);

    private async Task<OrderStatusUpdateResultDto> CancelAsync(
        Guid actorUserId,
        Guid orderId,
        UserRole actorRole,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty)
            throw new ValidationException("ACTOR_USER_ID_REQUIRED");

        if (orderId == Guid.Empty)
            throw new ValidationException("ORDER_ID_REQUIRED");

        // AdminOrderStatusService also acquires this exact lock.
        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:order:{orderId:N}",
            async ct =>
            {
                // Never trust the role supplied by the presentation layer.
                var actor = await unitOfWork.GetRepository<User>()
                    .GetByAsync(
                        x => x.Id == actorUserId &&
                             x.IsActive &&
                             x.Role == actorRole,
                        cancellationToken: ct);

                if (actor is null)
                    throw new BusinessException(
                        BusinessErrorType.Unauthorized,
                        "ORDER_CANCEL_ACCESS_REQUIRED");

                var order = await unitOfWork.GetRepository<Order>()
                    .GetByIdAsync(
                        orderId,
                        trackChanges: true,
                        cancellationToken: ct);

                // Return 404 for both missing and other customers' orders.
                if (order is null ||
                    (actorRole == UserRole.Customer &&
                     order.UserId != actorUserId))
                {
                    throw new BusinessException(
                        BusinessErrorType.NotFound,
                        "ORDER_NOT_FOUND");
                }

                // Repeat requests must NEVER restore stock a second time.
                if (order.Status == OrderStatus.Cancelled)
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "ORDER_ALREADY_CANCELLED");

                if (!OrderStatusTransitionPolicy.RequiresStockRestoration(
                    order.Status, OrderStatus.Cancelled, actorRole))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "INVALID_ORDER_STATUS_TRANSITION");
                }

                // Current version supports exactly one unpaid COD payment.
                var payments = await unitOfWork.GetRepository<Payment>()
                    .FindAsync(
                        x => x.OrderId == orderId,
                        trackChanges: true,
                        cancellationToken: ct);

                if (!OrderFinancialConsistency.HasValidCodPayment(
                        order, payments) ||
                    payments[0].Status != PaymentStatus.Pending ||
                    payments[0].PaidAt.HasValue)

                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "PAYMENT_NOT_CANCELLABLE");
                }

                var orderItems = await unitOfWork.GetRepository<OrderItem>()
                    .FindAsync(
                        x => x.OrderId == orderId,
                        cancellationToken: ct);

                if (!OrderFinancialConsistency.HasValidOrderItems(
                        order, orderItems))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "INVALID_ORDER_ITEMS");
                }

                // ExecuteUpdate uses the same DbContext and transaction.
                // A later failure rolls back all previous stock restores.
                foreach (var item in orderItems.OrderBy(x => x.ProductId))
                {
                    var restored = await stockRepository.TryIncreaseAsync(
                        item.ProductId, item.Quantity, ct);

                    if (!restored)
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "STOCK_RESTORATION_FAILED");
                }

                var now = DateTime.UtcNow;
                var previousStatus = order.Status;

                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = now;

                payments[0].Status = PaymentStatus.Cancelled;
                payments[0].UpdatedAt = now;

                // UoW saves tracked entities, commits, and releases the lock.
                return new OrderStatusUpdateResultDto(
                    order.Id,
                    order.OrderCode,
                    previousStatus,
                    order.Status,
                    now);
            },
            cancellationToken);
    }
}
