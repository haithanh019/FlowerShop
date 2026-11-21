using FlowerShop.Application.DTOs.Orders;

namespace FlowerShop.Application.Interfaces.Services;

public interface ICodPaymentSettlementService
{
    Task<CodPaymentSettlementResultDto> ConfirmCollectedAsync(
        Guid adminUserId,
        Guid orderId,
        CancellationToken cancellationToken = default);
}