using System.Data;
using FlowerShop.Domain.Common;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

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

    // Execute a series of operations in a transaction
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (_context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "NESTED_TRANSACTION_NOT_SUPPORTED");
        }

        await using var transaction =
            await _context.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        // Perform all business operations
        var result = await operation(cancellationToken);

        // Persist tracked entities before committing
        await _context.SaveChangesAsync(cancellationToken);

        // Commit only after everything succeeds
        await transaction.CommitAsync(cancellationToken);

        return result;
    }
}
