using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Concurrency;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Domain.Policies;

namespace FlowerShop.Application.Services;

public sealed class OrderCancellationService(
    IUnitOfWork unitOfWork,
    IProductStockRepository stockRepository,
    IVoucherUsageReadRepository voucherUsageRead,
    IVoucherUsageWriteRepository voucherUsageWrite)
    : IOrderCancellationService
{
    public Task<OrderStatusUpdateResultDto> CancelByCustomerAsync(
        Guid customerUserId, Guid orderId, CancellationToken cancellationToken = default)
        => CancelAsync(customerUserId, orderId, UserRole.Customer, cancellationToken);

    public Task<OrderStatusUpdateResultDto> CancelByAdminAsync(
        Guid adminUserId, Guid orderId, CancellationToken cancellationToken = default)
        => CancelAsync(adminUserId, orderId, UserRole.Admin, cancellationToken);

    private async Task<OrderStatusUpdateResultDto> CancelAsync(
        Guid actorUserId, Guid orderId, UserRole actorRole, CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty)
            throw new ValidationException("ACTOR_USER_ID_REQUIRED");
        if (orderId == Guid.Empty)
            throw new ValidationException("ORDER_ID_REQUIRED");

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:order:{orderId:N}", async ct =>
            {
                var actor = await unitOfWork.GetRepository<User>().GetByAsync(
                    x => x.Id == actorUserId && x.IsActive && x.Role == actorRole,
                    cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.Unauthorized, "ORDER_CANCEL_ACCESS_REQUIRED");
                var order = await unitOfWork.GetRepository<Order>().GetByIdAsync(
                    orderId, trackChanges: true, cancellationToken: ct);

                if (order is null || (actorRole == UserRole.Customer && order.UserId != actorUserId))
                    throw new BusinessException(BusinessErrorType.NotFound, "ORDER_NOT_FOUND");

                // A repeat cancellation must not restore stock or usage twice.
                if (order.Status == OrderStatus.Cancelled)
                    throw Conflict("ORDER_ALREADY_CANCELLED");
                if (!OrderStatusTransitionPolicy.RequiresStockRestoration(
                        order.Status, OrderStatus.Cancelled, actorRole))
                    throw Conflict("INVALID_ORDER_STATUS_TRANSITION");

                var payments = await unitOfWork.GetRepository<Payment>().FindAsync(
                    x => x.OrderId == orderId, trackChanges: true, cancellationToken: ct);
                if (!OrderFinancialConsistency.HasValidCodPayment(order, payments) ||
                    payments[0].Status != PaymentStatus.Pending || payments[0].PaidAt.HasValue)
                    throw Conflict("PAYMENT_NOT_CANCELLABLE");

                var orderItems = await unitOfWork.GetRepository<OrderItem>().FindAsync(
                    x => x.OrderId == orderId, cancellationToken: ct);
                if (!OrderFinancialConsistency.HasValidOrderItems(order, orderItems))
                    throw Conflict("INVALID_ORDER_ITEMS");

                // Order -> Voucher -> Product stock. Never acquire the voucher lock
                // after restoring product stock. All writes share the current transaction.
                await RestoreVoucherUsageAsync(order, ct);

                foreach (var item in orderItems.OrderBy(x => x.ProductId))
                {
                    if (!await stockRepository.TryIncreaseAsync(item.ProductId, item.Quantity, ct))
                        throw Conflict("STOCK_RESTORATION_FAILED");
                }

                var now = DateTime.UtcNow;
                var previousStatus = order.Status;
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = now;
                payments[0].Status = PaymentStatus.Cancelled;
                payments[0].UpdatedAt = now;

                // Keep VoucherId, fingerprint, monetary snapshots and cart unchanged.
                // UnitOfWork saves and commits, or rolls back all writes on failure.
                return new OrderStatusUpdateResultDto(order.Id, order.OrderCode,
                    previousStatus, order.Status, now);
            }, cancellationToken);
    }

    private async Task RestoreVoucherUsageAsync(Order order, CancellationToken ct)
    {
        if (order.VoucherId is not Guid voucherId)
            return;

        // A linked order must contain a genuine positive voucher discount.
        // Do not reinterpret or repair anomalous historical financial snapshots.
        if (voucherId == Guid.Empty || order.DiscountAmount <= 0m)
            throw Conflict("ORDER_VOUCHER_DATA_INVALID");

        await unitOfWork.AcquireTransactionLockAsync(VoucherLockKeys.ForId(voucherId), ct);

        // Fresh no-tracking read after taking the same lock used by redemption/admin.
        var voucher = await unitOfWork.GetRepository<Voucher>().GetByIdAsync(
            voucherId, trackChanges: false, cancellationToken: ct)
            ?? throw Conflict("ORDER_VOUCHER_DATA_INVALID");

        // This order is still non-cancelled, so its use must be included in the count.
        var actualUsedCount = await voucherUsageRead.CountNonCancelledOrdersAsync(
            voucherId, cancellationToken: ct);
        if (voucher.UsedCount <= 0 || actualUsedCount != voucher.UsedCount)
            throw Conflict("VOUCHER_USAGE_INCONSISTENT");

        // Do not evaluate current eligibility: inactive, expired and exhausted
        // vouchers must still release their previously committed uses.
        if (!await voucherUsageWrite.TryDecrementAsync(voucherId, voucher.UsedCount, ct))
            throw Conflict("VOUCHER_USAGE_RESTORATION_FAILED");
    }

    private static BusinessException Conflict(string code) => new(BusinessErrorType.Conflict, code);
}
