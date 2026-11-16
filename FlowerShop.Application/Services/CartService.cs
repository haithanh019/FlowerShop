
using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Carts;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces;

namespace FlowerShop.Application.Services;

public sealed class CartService(IUnitOfWork unitOfWork)
    : ICartService
{
    public Task AddItemAsync(
        Guid userId,
        AddCartItemDto dto,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCartWriteAsync(
            userId,
            ct => AddItemCoreAsync(userId, dto, ct),
            cancellationToken);
    }

    public Task UpdateQuantityAsync(
        Guid userId,
        Guid cartItemId,
        UpdateCartItemDto dto,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCartWriteAsync(
            userId,
            ct => UpdateQuantityCoreAsync(
                userId, cartItemId, dto, ct),
            cancellationToken);
    }

    public Task RemoveItemAsync(
        Guid userId,
        Guid cartItemId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCartWriteAsync(
            userId,
            ct => RemoveItemCoreAsync(
                userId, cartItemId, ct),
            cancellationToken);
    }

    private async Task ExecuteCartWriteAsync(
        Guid userId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ValidateUserId(userId);

        await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:cart:{userId:N}",
            async ct =>
            {
                await operation(ct);
                return true;
            },
            cancellationToken);
    }

    private async Task AddItemCoreAsync(
        Guid userId,
        AddCartItemDto dto,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto, new ValidationContext(dto), true);

        if (dto.ProductId == Guid.Empty)
            throw new ValidationException("PRODUCT_ID_REQUIRED");

        var product = await GetAvailableProductAsync(
            dto.ProductId, cancellationToken);

        EnsureStock(product, dto.Quantity);

        var cartRepository = unitOfWork.GetRepository<Cart>();
        var itemRepository = unitOfWork.GetRepository<CartItem>();

        var cart = await cartRepository.GetByAsync(
            x => x.UserId == userId,
            trackChanges: true,
            cancellationToken: cancellationToken);

        var isNewCart = cart is null;

        if (cart is null)
        {
            cart = new Cart { UserId = userId };

            await cartRepository.AddAsync(
                cart, cancellationToken);
        }

        CartItem? existingItem = null;

        if (!isNewCart)
        {
            existingItem = await itemRepository.GetByAsync(
                x => x.CartId == cart.Id &&
                     x.ProductId == dto.ProductId,
                trackChanges: true,
                cancellationToken: cancellationToken);
        }

        if (existingItem is null)
        {
            var item = new CartItem
            {
                Cart = cart,
                CartId = cart.Id,
                ProductId = dto.ProductId,
                Quantity = dto.Quantity
            };

            await itemRepository.AddAsync(
                item, cancellationToken);
        }
        else
        {
            if (existingItem.Quantity >
                product.StockQuantity - dto.Quantity)
            {
                throw new BusinessException(
                    BusinessErrorType.Conflict,
                    "INSUFFICIENT_STOCK");
            }

            existingItem.Quantity += dto.Quantity;
            existingItem.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task UpdateQuantityCoreAsync(
        Guid userId,
        Guid cartItemId,
        UpdateCartItemDto dto,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto, new ValidationContext(dto), true);

        var item = await GetOwnedItemAsync(
            userId, cartItemId, cancellationToken);

        var product = await GetAvailableProductAsync(
            item.ProductId, cancellationToken);

        EnsureStock(product, dto.Quantity);

        if (item.Quantity == dto.Quantity)
            return;

        item.Quantity = dto.Quantity;
        item.UpdatedAt = DateTime.UtcNow;
    }

    private async Task RemoveItemCoreAsync(
        Guid userId,
        Guid cartItemId,
        CancellationToken cancellationToken)
    {
        var item = await GetOwnedItemAsync(
            userId, cartItemId, cancellationToken);

        unitOfWork.GetRepository<CartItem>().Delete(item);
    }

    private async Task<CartItem> GetOwnedItemAsync(
        Guid userId,
        Guid cartItemId,
        CancellationToken cancellationToken)
    {
        var item = await unitOfWork.GetRepository<CartItem>()
            .GetByAsync(
                x => x.Id == cartItemId &&
                     x.Cart.UserId == userId,
                trackChanges: true,
                cancellationToken: cancellationToken);

        return item ?? throw new BusinessException(
            BusinessErrorType.NotFound,
            "CART_ITEM_NOT_FOUND");
    }

    private async Task<Product> GetAvailableProductAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var product = await unitOfWork.GetRepository<Product>()
            .GetByIdAsync(
                productId,
                cancellationToken: cancellationToken) ?? throw new BusinessException(
                BusinessErrorType.NotFound,
                "PRODUCT_NOT_FOUND");
        if (!product.IsActive)
            throw new BusinessException(
                BusinessErrorType.Conflict,
                "PRODUCT_INACTIVE");

        return product;
    }

    private static void EnsureStock(
        Product product,
        int quantity)
    {
        if (quantity <= 0)
            throw new ValidationException(
                "QUANTITY_MUST_BE_POSITIVE");

        if (quantity > product.StockQuantity)
            throw new BusinessException(
                BusinessErrorType.Conflict,
                "INSUFFICIENT_STOCK");
    }

    private static void ValidateUserId(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");
    }
}
