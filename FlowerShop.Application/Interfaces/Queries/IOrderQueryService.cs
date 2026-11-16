using FlowerShop.Application.DTOs.Orders;

namespace FlowerShop.Application.Interfaces.Queries;

public interface IOrderQueryService
{
    Task<OrderHistoryDto> GetHistoryAsync(
        Guid userId,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default);

    Task<OrderDetailDto> GetDetailAsync(
        Guid userId,
        Guid orderId,
        CancellationToken cancellationToken = default);
}
