using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Domain.Policies;
using FlowerShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class CustomerVoucherReadRepository(
    FlowerShopDbContext db,
    IReadOnlyQueryRepository<Order> orders)
    : ICustomerVoucherReadRepository
{
    private const string CanonicalVoucherSql = """
        SELECT v.*
        FROM dbo.Vouchers AS v
        WHERE DATALENGTH(v.Code) BETWEEN 2 AND 100
          AND CONVERT(varbinary(100), TRANSLATE(
                v.Code COLLATE Latin1_General_100_BIN2,
                N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-',
                REPLICATE(N'A', 38)))
              = CONVERT(varbinary(100),
                  REPLICATE(N'A', DATALENGTH(v.Code) / 2))
        """;

    public async Task<(
        IReadOnlyList<Voucher> Vouchers,
        int TotalCount)> GetAvailablePageAsync(
        Guid userId,
        DateTime nowUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException
            .ThrowIfNegativeOrZero(page);

        if (pageSize is < 1 or > 50)
            throw new ArgumentOutOfRangeException(
                nameof(pageSize));

        var offset = checked((page - 1) * pageSize);

        var committed = orders.Query()
            .Where(x => x.Status != OrderStatus.Cancelled);

        var query = db.Vouchers
            .FromSqlRaw(CanonicalVoucherSql)
            .AsNoTracking()

            .Where(VoucherAvailabilityPolicy.MatchesStatus(
                VoucherAvailabilityStatus.Available,
                nowUtc))

            .Where(x =>
                x.DiscountType != DiscountType.FixedAmount ||
                x.MaxDiscountAmount == null)

            .Where(x => committed.LongCount(
                o => o.VoucherId == x.Id) == x.UsedCount)

            .Where(x => !x.PerUserLimit.HasValue ||
                committed.LongCount(o =>
                    o.VoucherId == x.Id &&
                    o.UserId == userId) < x.PerUserLimit.Value);

        var count = await query.CountAsync(
            cancellationToken);

        var rows = await query
            .OrderBy(x => x.EndAt)
            .ThenBy(x => x.Code)
            .ThenBy(x => x.Id)
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (rows, count);
    }
}
