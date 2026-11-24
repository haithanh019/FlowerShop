using FlowerShop.Application.DTOs.Vouchers;

namespace FlowerShop.Application.Interfaces.Services;

public interface IVoucherService
{
    Task<VoucherDto> CreateAsync(
        Guid adminUserId,
        CreateVoucherDto dto,
        CancellationToken cancellationToken = default);

    Task<VoucherDto> UpdateAsync(
        Guid adminUserId,
        Guid voucherId,
        UpdateVoucherDto dto,
        CancellationToken cancellationToken = default);

    Task<VoucherDto> UpdateStatusAsync(
        Guid adminUserId,
        Guid voucherId,
        UpdateVoucherStatusDto dto,
        CancellationToken cancellationToken = default);
}
