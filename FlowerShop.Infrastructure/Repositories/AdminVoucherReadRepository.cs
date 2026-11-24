using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Domain.Policies;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class AdminVoucherReadRepository(
    IReadOnlyQueryRepository<Voucher> vouchers) : IAdminVoucherReadRepository
{
    public async Task<(IReadOnlyList<Voucher> Vouchers, int TotalCount)> GetPageAsync(
        string? codeKeyword,
        DiscountType? discountType,
        VoucherAvailabilityStatus? status,
        VoucherSortOrder sort,
        DateTime nowUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        if (pageSize is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (discountType.HasValue && !Enum.IsDefined(discountType.Value))
            throw new ArgumentOutOfRangeException(nameof(discountType));
        if (status.HasValue && !Enum.IsDefined(status.Value))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));

        var offset = checked((page - 1) * pageSize);
        var query = vouchers.Query(); // AsNoTracking()

        if (!string.IsNullOrEmpty(codeKeyword))
            query = query.Where(x => x.Code.Contains(codeKeyword));
        if (discountType.HasValue)
            query = query.Where(x => x.DiscountType == discountType.Value);
        if (status.HasValue)
            query = query.Where(VoucherAvailabilityPolicy.MatchesStatus(
                status.Value, nowUtc));

        var totalCount = await query.CountAsync(cancellationToken);

        // A stable tie-breaker keeps ordering deterministic for unchanged data.
        var sorted = sort switch
        {
            VoucherSortOrder.Newest => query.OrderByDescending(x => x.CreatedAt)
                .ThenBy(x => x.Code).ThenBy(x => x.Id),
            VoucherSortOrder.Oldest => query.OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Code).ThenBy(x => x.Id),
            VoucherSortOrder.CodeAscending => query.OrderBy(x => x.Code)
                .ThenBy(x => x.Id),
            VoucherSortOrder.CodeDescending => query.OrderByDescending(x => x.Code)
                .ThenBy(x => x.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };

        var items = await sorted.Skip(offset).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, totalCount);
    }

    public Task<Voucher?> GetByIdAsync(
        Guid voucherId, CancellationToken cancellationToken = default)
    {
        if (voucherId == Guid.Empty)
            throw new ArgumentException("VOUCHER_ID_REQUIRED", nameof(voucherId));

        return vouchers.Query().FirstOrDefaultAsync(
            x => x.Id == voucherId, cancellationToken);
    }
}
