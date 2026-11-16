using FlowerShop.Domain.Common;

namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IReadOnlyQueryRepository<T>
    where T : BaseEntity
{
    IQueryable<T> Query();
}
