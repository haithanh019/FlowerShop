using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.Interfaces.Queries;

public interface IAdminOrderQueryService
{
    Task<AdminOrderPageDto> GetPageAsync(
        Guid adminUserId,
        OrderStatus? status = null,
        Guid? customerUserId = null,
        string? orderCodeKeyword = null,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default);

    Task<AdminOrderDetailDto> GetDetailAsync(
        Guid adminUserId,
        Guid orderId,
        CancellationToken cancellationToken = default);
}
