using FlowerShop.Application.DTOs.Orders;

namespace FlowerShop.Application.Interfaces.Services;

public interface ICheckoutService
{
    Task<CheckoutResultDto> CheckoutAsync(
        Guid userId,
        CheckoutRequestDto dto,
        CancellationToken cancellationToken = default);
}
