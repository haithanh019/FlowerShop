using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Policies;

namespace FlowerShop.Application.Services;

public sealed class CodPaymentSettlementService(
    IUnitOfWork unitOfWork)
    : ICodPaymentSettlementService
{
    public async Task<CodPaymentSettlementResultDto>
        ConfirmCollectedAsync(
            Guid adminUserId,
            Guid orderId,
            CancellationToken cancellationToken = default)
    {
        // 1. Validate input before opening a transaction.
        if (adminUserId == Guid.Empty)
            throw new ValidationException(
                "ADMIN_USER_ID_REQUIRED");

        if (orderId == Guid.Empty)
            throw new ValidationException(
                "ORDER_ID_REQUIRED");

        // 2. Serialize settlement, cancellation and status updates
        // using the same lock resource for this Order.
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

                // 4. Load the persisted Order.
                var order = await unitOfWork
                    .GetRepository<Order>()
                    .GetByIdAsync(
                        orderId,
                        cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.NotFound,
                        "ORDER_NOT_FOUND");

                // 5. Load COD Payment with change tracking.
                var payments = await unitOfWork
                    .GetRepository<Payment>()
                    .FindAsync(
                        x => x.OrderId == orderId,
                        trackChanges: true,
                        cancellationToken: ct);

                // Validate the financial snapshot first.
                if (!OrderFinancialConsistency.HasValidCodPayment(
                        order, payments))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "ORDER_PAYMENT_STATE_INVALID");
                }

                var payment = payments[0];

                // 6. Enforce COD lifecycle policy.
                if (!CodPaymentLifecyclePolicy.CanConfirmCollection(
                        order.Status,
                        payment.Method,
                        payment.Status,
                        payment.PaidAt))
                {
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "COD_PAYMENT_NOT_SETTLEABLE");
                }

                // 7. Validate persisted OrderItems.
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

                // 8. Confirm COD collection.
                var now = DateTime.UtcNow;

                payment.Status = PaymentStatus.Paid;
                payment.PaidAt = now;
                payment.UpdatedAt = now;

                // Do not change Order.Status or Product stock.

                // 9. UnitOfWork saves and commits before returning.
                return new CodPaymentSettlementResultDto(
                    order.Id,
                    order.OrderCode,
                    payment.Id,
                    payment.Status,
                    payment.Amount,
                    payment.Currency,
                    now,
                    now);
            },
            cancellationToken);
    }
}
