using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Domain.Policies;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class CustomerVoucherReadRepository(
    IReadOnlyQueryRepository<Voucher> vouchers,
    IReadOnlyQueryRepository<Order> orders) : ICustomerVoucherReadRepository
{
    public async Task<(IReadOnlyList<Voucher> Vouchers, int TotalCount)> GetAvailablePageAsync(
        Guid userId, DateTime nowUtc, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        if (pageSize is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        var offset = checked((page - 1) * pageSize);

        var committed = orders.Query().Where(x => x.Status != OrderStatus.Cancelled);
        var query = vouchers.Query()
            .Where(VoucherAvailabilityPolicy.MatchesStatus(VoucherAvailabilityStatus.Available, nowUtc))
            // Existing database constraints protect the other monetary/limit fields.
            .Where(x => x.DiscountType != DiscountType.FixedAmount || x.MaxDiscountAmount == null)
            // Exclude noncanonical legacy codes on SQL Server (nvarchar, binary collation).
            // LEN ignores trailing spaces; DATALENGTH catches them.
            .Where(x => x.Code.Length > 0 &&
                EF.Functions.DataLength(x.Code) == x.Code.Length * 2 &&
                !EF.Functions.Like(EF.Functions.Collate(x.Code, "Latin1_General_100_BIN2"), "%[^A-Z0-9_-]%"))
            // A quote/list is not a reservation; fail closed on counter mismatch.
            .Where(x => committed.LongCount(o => o.VoucherId == x.Id) == x.UsedCount)
            .Where(x => !x.PerUserLimit.HasValue ||
                committed.LongCount(o => o.VoucherId == x.Id && o.UserId == userId) < x.PerUserLimit.Value);

        var count = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.EndAt)
            .ThenBy(x => x.Code).ThenBy(x => x.Id)
            .Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        return (rows, count);
    }
}
