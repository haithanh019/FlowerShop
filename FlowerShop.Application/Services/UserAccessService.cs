
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class UserAccessService(
    IGenericRepository<User> users) : IUserAccessService
{
    public async Task<bool> IsActiveWithRoleAsync(
        Guid userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        var user = await users.GetByIdAsync(
            userId,
            cancellationToken: cancellationToken);

        return user is { IsActive: true }
            && user.Role.ToString() == role;
    }
}
