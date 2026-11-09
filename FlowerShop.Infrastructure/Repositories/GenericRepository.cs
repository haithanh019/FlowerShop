using System.Linq.Expressions;
using FlowerShop.Domain.Common;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Infrastructure.Repositories;

public class GenericRepository<T> : IGenericRepository<T>
    where T : BaseEntity
{
    private readonly DbSet<T> _dbSet;

    public GenericRepository(FlowerShopDbContext context)
    {
        _dbSet = context.Set<T>();
    }

    // Get entity by ID
    public async Task<T?> GetByIdAsync(
        Guid id,
        bool trackChanges = false,
        CancellationToken cancellationToken = default)
    {
        return await Query(trackChanges)
            .FirstOrDefaultAsync(
                x => x.Id == id,
                cancellationToken);
    }

    // Get first entity matching a condition
    public async Task<T?> GetByAsync(
        Expression<Func<T, bool>> predicate,
        bool trackChanges = false,
        CancellationToken cancellationToken = default)
    {
        return await Query(trackChanges)
            .FirstOrDefaultAsync(
                predicate,
                cancellationToken);
    }

    // Get all entities
    public async Task<IReadOnlyList<T>> GetAllAsync(
        bool trackChanges = false,
        CancellationToken cancellationToken = default)
    {
        return await Query(trackChanges)
            .ToListAsync(cancellationToken);
    }

    // Find entities matching a condition
    public async Task<IReadOnlyList<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        bool trackChanges = false,
        CancellationToken cancellationToken = default)
    {
        return await Query(trackChanges)
            .Where(predicate)
            .ToListAsync(cancellationToken);
    }

    // Add entity
    public async Task AddAsync(
        T entity,
        CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
    }

    // Update entity
    public void Update(T entity)
    {
        _dbSet.Update(entity);
    }

    // Delete entity
    public void Delete(T entity)
    {
        _dbSet.Remove(entity);
    }

    // Configure tracking behavior
    private IQueryable<T> Query(bool trackChanges)
    {
        return trackChanges
            ? _dbSet.AsTracking()
            : _dbSet.AsNoTracking();
    }
}
