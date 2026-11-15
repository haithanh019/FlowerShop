namespace FlowerShop.Application.Interfaces.Services;

public interface IUserAccessService
{
    Task<bool> IsActiveWithRoleAsync(
        Guid userId,
        string role,
        CancellationToken cancellationToken = default);
}