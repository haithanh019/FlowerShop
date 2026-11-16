using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.ProductImages;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class ProductImageService(
    IUnitOfWork unitOfWork,
    IImageStorageRepository imageStorage,
    IProductImagePrimaryRepository primaryRepository)
    : IProductImageService
{
    private const long MaxFileSize = 5 * 1024 * 1024;

    public async Task<ProductImageDto> UploadAsync(
        Guid productId,
        Stream stream,
        string fileName,
        string contentType,
        long fileLength,
        CancellationToken cancellationToken = default)
    {
        // Validate metadata before processing the file
        if (stream is null || fileLength <= 0 || fileLength > MaxFileSize)
            throw new ValidationException("INVALID_IMAGE_SIZE");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        var expectedMime = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => throw new ValidationException("INVALID_IMAGE_TYPE")
        };

        if (!string.Equals(contentType, expectedMime,
                StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("INVALID_IMAGE_TYPE");

        // Product must exist
        _ = await unitOfWork.GetRepository<Product>()
            .GetByIdAsync(productId, cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_NOT_FOUND");

        // Buffer file with a hard size limit
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(
            chunk.AsMemory(), cancellationToken)) > 0)
        {
            if (buffer.Length + bytesRead > MaxFileSize)
                throw new ValidationException("INVALID_IMAGE_SIZE");

            await buffer.WriteAsync(
                chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        if (buffer.Length != fileLength)
            throw new ValidationException("INVALID_IMAGE_SIZE");

        // Verify the actual file signature
        buffer.Position = 0;
        var header = new byte[12];
        var headerLength = await buffer.ReadAsync(
            header.AsMemory(), cancellationToken);

        var valid = extension switch
        {
            ".jpg" or ".jpeg" =>
                headerLength >= 3 &&
                header[0] == 0xFF &&
                header[1] == 0xD8 &&
                header[2] == 0xFF,

            ".png" =>
                headerLength >= 8 &&
                header.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),

            ".webp" =>
                headerLength >= 12 &&
                header.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                header.AsSpan(8, 4).SequenceEqual("WEBP"u8),

            _ => false
        };

        if (!valid)
            throw new ValidationException("INVALID_IMAGE_SIGNATURE");

        buffer.Position = 0;

        // Generate a safe filename
        var safeName = $"{Guid.NewGuid():N}{extension}";

        // Upload to Cloudinary
        var (imageUrl, publicId) = await imageStorage.UploadAsync(
            buffer, safeName, cancellationToken);

        var image = new ProductImage
        {
            ProductId = productId,
            ImageUrl = imageUrl,
            PublicId = publicId,
            IsPrimary = false,
            SortOrder = 0
        };

        // Save database record; compensate if saving fails
        try
        {
            await unitOfWork.GetRepository<ProductImage>()
                .AddAsync(image, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception saveError)
        {
            try
            {
                await imageStorage.DeleteAsync(publicId);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "PRODUCT_IMAGE_SAVE_AND_CLEANUP_FAILED",
                    saveError,
                    cleanupError);
            }

            throw;
        }

        return new ProductImageDto(
            image.Id,
            image.ProductId,
            image.ImageUrl,
            image.IsPrimary,
            image.SortOrder,
            image.CreatedAt);
    }

    public async Task DeleteAsync(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<ProductImage>();

        var image = await repository.GetByAsync(
            x => x.Id == imageId && x.ProductId == productId,
            trackChanges: true,
            cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_IMAGE_NOT_FOUND");

        // Delete from Cloudinary first
        if (!string.IsNullOrWhiteSpace(image.PublicId))
            await imageStorage.DeleteAsync(image.PublicId);

        // Delete database record only after storage deletion succeeds
        repository.Delete(image);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }


    public async Task SetPrimaryAsync(
        Guid productId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        var image = await unitOfWork
            .GetRepository<ProductImage>()
            .GetByAsync(
                x => x.Id == imageId && x.ProductId == productId,
                cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_IMAGE_NOT_FOUND");
        var success = await primaryRepository.SetPrimaryAsync(
            productId, imageId, cancellationToken);

        if (!success)
            throw new BusinessException(
                BusinessErrorType.Conflict, "PRIMARY_IMAGE_CONFLICT");
    }


    public async Task ReorderAsync(
        Guid productId,
        IReadOnlyList<Guid> orderedImageIds,
        CancellationToken cancellationToken = default)
    {
        if (orderedImageIds is null)
            throw new ValidationException("INVALID_IMAGE_ORDER");

        var product = await unitOfWork.GetRepository<Product>()
            .GetByIdAsync(productId, cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound, "PRODUCT_NOT_FOUND");
        var images = await unitOfWork.GetRepository<ProductImage>()
            .FindAsync(
                x => x.ProductId == productId,
                trackChanges: true,
                cancellationToken: cancellationToken);

        var ids = orderedImageIds.ToHashSet();

        if (ids.Count != orderedImageIds.Count ||
            ids.Count != images.Count ||
            images.Any(x => !ids.Contains(x.Id)))
        {
            throw new ValidationException("INVALID_IMAGE_ORDER");
        }

        var lookup = images.ToDictionary(x => x.Id);
        var now = DateTime.UtcNow;

        for (var i = 0; i < orderedImageIds.Count; i++)
        {
            var image = lookup[orderedImageIds[i]];
            image.SortOrder = i;
            image.UpdatedAt = now;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

}
