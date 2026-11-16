namespace FlowerShop.Domain.Interfaces.Repositories;

public interface IImageStorageRepository
{
    Task<(string ImageUrl, string PublicId)> UploadAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string publicId);
}
