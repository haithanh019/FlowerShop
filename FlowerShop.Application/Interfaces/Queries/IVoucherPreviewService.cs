using FlowerShop.Application.DTOs.Vouchers;

namespace FlowerShop.Application.Interfaces.Services;

public interface IVoucherPreviewService
{
    Task<VoucherPreviewResultDto> PreviewAsync(
        Guid userId,
        VoucherPreviewRequestDto dto,
        CancellationToken cancellationToken = default);
}
