using FlowerShop.Domain.Common;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Domain.Interfaces;

public interface IUnitOfWork
{
    IGenericRepository<T> GetRepository<T>()
        where T : BaseEntity;

    Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default);

    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
