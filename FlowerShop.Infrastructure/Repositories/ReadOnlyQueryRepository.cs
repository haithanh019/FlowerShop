using FlowerShop.Domain.Common;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class ReadOnlyQueryRepository<T>
    : IReadOnlyQueryRepository<T>
    where T : BaseEntity
{
    private readonly FlowerShopDbContext _context;

    public ReadOnlyQueryRepository(FlowerShopDbContext context)
    {
        _context = context;
    }

    public IQueryable<T> Query()
    {
        return _context.Set<T>().AsNoTracking();
    }
}
