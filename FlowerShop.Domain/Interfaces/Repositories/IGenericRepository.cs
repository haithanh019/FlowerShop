using System.Linq.Expressions;
using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IGenericRepository<T> where T : BaseEntity
{
    // READ
    Task<T?> GetByIdAsync(
        Guid id,
        bool trackChanges = false,
        CancellationToken cancellationToken = default);

    Task<T?> GetByAsync(
        Expression<Func<T, bool>> predicate,
        bool trackChanges = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> GetAllAsync(
        bool trackChanges = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        bool trackChanges = false,
        CancellationToken cancellationToken = default);

    // CREATE
    Task AddAsync(
        T entity,
        CancellationToken cancellationToken = default);

    // UPDATE
    void Update(T entity);

    // DELETE
    void Delete(T entity);
}
