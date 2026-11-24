using FlowerShop.Application.DTOs.UserProfiles;

namespace FlowerShop.Application.Interfaces.Services;

public interface IUserProfileService
{
    Task<UserProfileDto> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
