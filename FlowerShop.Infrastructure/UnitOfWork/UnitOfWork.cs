using FlowerShop.Domain.Common;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.Infrastructure.Repositories;

namespace FlowerShop.Infrastructure.UnitOfWork;

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly FlowerShopDbContext _context;

    public UnitOfWork(FlowerShopDbContext context)
    {
        _context = context;
    }

    // Get repository for an entity
    public IGenericRepository<T> GetRepository<T>()
        where T : BaseEntity
    {
        return new GenericRepository<T>(_context);
    }

    // Save all tracked changes
    public Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }
}
