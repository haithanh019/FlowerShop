using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Cloudinary;
using Microsoft.Extensions.Options;
using CloudinaryClient = CloudinaryDotNet.Cloudinary;

namespace FlowerShop.Infrastructure.Repositories;

public sealed class CloudinaryImageRepository
    : IImageStorageRepository
{
    private readonly CloudinaryClient _cloudinary;

    public CloudinaryImageRepository(
        IOptions<CloudinarySettings> options)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.CloudName) ||
            string.IsNullOrWhiteSpace(settings.ApiKey) ||
            string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            throw new InvalidOperationException(
                "Cloudinary configuration is missing.");
        }

        _cloudinary = new CloudinaryClient(new Account(
            settings.CloudName,
            settings.ApiKey,
            settings.ApiSecret));

        _cloudinary.Api.Secure = true;
    }

    public async Task<(string ImageUrl, string PublicId)> UploadAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var uploadParams = new ImageUploadParams
        {
            File = new FileDescription(
                Path.GetFileName(fileName), stream),

            Folder = "flower-shop/products",
            UseFilename = false,
            UniqueFilename = true,
            Overwrite = false
        };

        var result = await _cloudinary.UploadAsync(
            uploadParams, cancellationToken);

        if (result.Error is not null ||
            result.SecureUrl is null ||
            string.IsNullOrWhiteSpace(result.PublicId))
        {
            throw new InvalidOperationException(
                "CLOUDINARY_UPLOAD_FAILED");
        }

        return (
            result.SecureUrl.ToString(),
            result.PublicId);
    }

    public async Task DeleteAsync(string publicId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);

        var result = await _cloudinary.DestroyAsync(
            new DeletionParams(publicId)
            {
                Invalidate = true
            });

        if (result.Error is not null ||
            result.Result is not ("ok" or "not found"))
        {
            throw new InvalidOperationException(
                "CLOUDINARY_DELETE_FAILED");
        }
    }
}
