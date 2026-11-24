using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IAdminVoucherReadRepository
{
    Task<(IReadOnlyList<Voucher> Vouchers, int TotalCount)> GetPageAsync(
        string? codeKeyword,
        DiscountType? discountType,
        VoucherAvailabilityStatus? status,
        VoucherSortOrder sort,
        DateTime nowUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Voucher?> GetByIdAsync(
        Guid voucherId,
        CancellationToken cancellationToken = default);
}
