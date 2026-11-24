using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class CustomerVoucherCanonicalCodeTests
{
    [Fact]
    public async Task Available_ShouldIncludeOnlyExactCanonicalAsciiCodesBeforePagination()
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ICustomerVoucherReadRepository>();
        var now = new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var userId = Guid.NewGuid();
        var prefix = $"CF-{Guid.NewGuid():N}-".ToUpperInvariant();

        string[] valid = [prefix + "A", prefix + "_B-09", prefix + new string('Z', 50 - prefix.Length)];
        string[] invalid = [prefix + "lower", prefix + "SPACE ", " " + prefix + "LEADING",
            prefix + "INNER SPACE", prefix + "P%", prefix + "BR[", prefix + "UNIC\u00C9",
            prefix + "WIDE\uFF21", prefix + "TAB\t", prefix + "ZERO\u200B"];
        var codes = valid.Concat(invalid).ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var baseline = (await repository.GetAvailablePageAsync(userId, now, 1, 1)).TotalCount;
            db.Vouchers.AddRange(codes.Select(code => new Voucher
            {
                Code = code, DiscountType = DiscountType.Percentage, DiscountValue = 20m,
                StartAt = now, EndAt = now.AddDays(1), IsActive = true, UsedCount = 0,
                UsageLimit = 10, PerUserLimit = 1
            }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            const int pageSize = 2;
            var firstPage = await repository.GetAvailablePageAsync(userId, now, 1, pageSize);
            Assert.Equal(baseline + valid.Length, firstPage.TotalCount);
            var returned = firstPage.Vouchers.Select(x => x.Code).ToList();
            var pageCount = 1 + (firstPage.TotalCount - 1) / pageSize;
            for (var page = 2; page <= pageCount; page++)
            {
                var next = await repository.GetAvailablePageAsync(userId, now, page, pageSize);
                Assert.Equal(firstPage.TotalCount, next.TotalCount);
                returned.AddRange(next.Vouchers.Select(x => x.Code));
            }

            var seeded = returned.Where(code => codes.Contains(code, StringComparer.Ordinal))
                .OrderBy(code => code, StringComparer.Ordinal).ToArray();
            Assert.Equal(valid.OrderBy(code => code, StringComparer.Ordinal).ToArray(), seeded);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }
}
