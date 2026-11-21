using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Policies;
using FlowerShop.Application.Common.Validation;

namespace FlowerShop.Application.Services;

public sealed class AdminOrderStatusService(
    IUnitOfWork unitOfWork)
    : IAdminOrderStatusService
{
    public async Task<OrderStatusUpdateResultDto> UpdateStatusAsync(
        Guid adminUserId,
        Guid orderId,
        OrderStatus targetStatus,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate input before opening transaction.
        if (adminUserId == Guid.Empty)
            throw new ValidationException(
                "ADMIN_USER_ID_REQUIRED");

        if (orderId == Guid.Empty)
            throw new ValidationException(
                "ORDER_ID_REQUIRED");

        if (!Enum.IsDefined(targetStatus))
            throw new ValidationException(
                "INVALID_ORDER_STATUS");

        // Cancellation requires stock restoration and
        // payment handling. It has a separate workflow.
        if (targetStatus == OrderStatus.Cancelled)
            throw new BusinessException(
                BusinessErrorType.Conflict,
                "USE_ORDER_CANCELLATION");

        // 2. Serialize changes to this Order.
        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:order:{orderId:N}",
            async ct =>
            {
                // 3. Verify the acting Admin account.
                var admin = await unitOfWork
                    .GetRepository<User>()
                    .GetByAsync(
                        x => x.Id == adminUserId &&
                             x.IsActive &&
                             x.Role == UserRole.Admin,
                        cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.Unauthorized,
                        "ADMIN_ACCESS_REQUIRED");

                // 4. Load Order with tracking.
                var order = await unitOfWork
                    .GetRepository<Order>()
                    .GetByIdAsync(
                        orderId,
                        trackChanges: true,
                        cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.NotFound,
                        "ORDER_NOT_FOUND");

                // 5. Validate the transition against Domain Policy.
                if (!OrderStatusTransitionPolicy.CanTransition(
                    order.Status,
                    targetStatus,
                    UserRole.Admin))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "INVALID_ORDER_STATUS_TRANSITION");
                }


                // Validate payment before allowing order progression.
                // The MVP supports one COD payment per order.
                var payments = await unitOfWork
                    .GetRepository<Payment>()
                    .FindAsync(
                        x => x.OrderId == orderId,
                        cancellationToken: ct);

                if (!OrderFinancialConsistency.HasValidCodPayment(
                        order, payments) ||
                    payments[0].Status is not (
                        PaymentStatus.Pending or PaymentStatus.Paid))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "ORDER_PAYMENT_STATE_INVALID");
                }


                // Before delivery, COD must remain unpaid.
                // A Paid payment or an unexpected PaidAt timestamp
                // indicates an inconsistent order/payment snapshot.
                if (targetStatus != OrderStatus.Delivered &&
                    (payments[0].Status != PaymentStatus.Pending ||
                     payments[0].PaidAt.HasValue))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "ORDER_PAYMENT_STATE_INVALID");
                }

                // Validate persisted OrderItems before progression.
                // The sum of item prices must match Order.Subtotal.
                var orderItems = await unitOfWork
                    .GetRepository<OrderItem>()
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


                // Delivered requires confirmed COD collection.
                // Do not mark an Order as delivered while COD
                // is unpaid or PaidAt is missing.
                if (targetStatus == OrderStatus.Delivered &&
                    !CodPaymentLifecyclePolicy.CanMarkDelivered(
                        order.Status,
                        payments[0].Method,
                        payments[0].Status,
                        payments[0].PaidAt))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "COD_PAYMENT_NOT_SETTLED");
                }



                // 6. Update only the tracked Order.
                var previousStatus = order.Status;
                var now = DateTime.UtcNow;

                order.Status = targetStatus;
                order.UpdatedAt = now;

                // UnitOfWork automatically calls SaveChangesAsync()
                // and CommitAsync() before returning this result.
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
