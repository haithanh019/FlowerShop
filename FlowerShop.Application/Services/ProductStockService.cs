using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class ProductStockService(
    IProductStockRepository stockRepository,
    IGenericRepository<Product> products)
    : IProductStockService
{
    public async Task IncreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ValidateQuantity(quantity);

        await GetProductAsync(productId, cancellationToken);

        var success = await stockRepository.TryIncreaseAsync(
            productId, quantity, cancellationToken);

        if (!success)
            throw new BusinessException(
                BusinessErrorType.Conflict, "STOCK_LIMIT_EXCEEDED");
    }

    public async Task DecreaseAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ValidateQuantity(quantity);

        var product = await GetProductAsync(
            productId, cancellationToken);

        if (!product.IsActive)
            throw new BusinessException(
                BusinessErrorType.Conflict, "PRODUCT_INACTIVE");

        var success = await stockRepository.TryDecreaseAsync(
            productId, quantity, cancellationToken);

        if (!success)
            throw new BusinessException(
                BusinessErrorType.Conflict, "INSUFFICIENT_STOCK");
    }

    private async Task<Product> GetProductAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(
            id, cancellationToken: cancellationToken);

        return product ?? throw new BusinessException(
            BusinessErrorType.NotFound, "PRODUCT_NOT_FOUND");
    }

    private static void ValidateQuantity(int quantity)
    {
        if (quantity <= 0)
            throw new ValidationException(
                "QUANTITY_MUST_BE_POSITIVE");
    }
}
