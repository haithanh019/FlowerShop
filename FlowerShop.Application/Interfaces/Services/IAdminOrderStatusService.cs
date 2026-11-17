using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.Interfaces.Services;

public interface IAdminOrderStatusService
{
    Task<OrderStatusUpdateResultDto> UpdateStatusAsync(
        Guid adminUserId,
        Guid orderId,
        OrderStatus targetStatus,
        CancellationToken cancellationToken = default);
}
