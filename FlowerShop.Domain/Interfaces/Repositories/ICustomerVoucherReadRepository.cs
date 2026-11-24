using FlowerShop.Domain.Entities;

namespace FlowerShop.Domain.Interfaces.Repositories;

public interface ICustomerVoucherReadRepository
{
    Task<(IReadOnlyList<Voucher> Vouchers, int TotalCount)> GetAvailablePageAsync(
        Guid userId, DateTime nowUtc, int page, int pageSize,
        CancellationToken cancellationToken = default);
}
