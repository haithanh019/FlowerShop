using FlowerShop.Application.DTOs.Orders;

namespace FlowerShop.Application.Interfaces.Services;

public interface IOrderCancellationService
{
    Task<OrderStatusUpdateResultDto> CancelByCustomerAsync(
        Guid customerUserId,
        Guid orderId,
        CancellationToken cancellationToken = default);

    Task<OrderStatusUpdateResultDto> CancelByAdminAsync(
        Guid adminUserId,
        Guid orderId,
        CancellationToken cancellationToken = default);
}
