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
    public async Task AddItemAsync(
        Guid userId,
        AddCartItemDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);
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

        // Find existing cart
        var cart = await cartRepository.GetByAsync(
            x => x.UserId == userId,
            trackChanges: true,
            cancellationToken: cancellationToken);

        var isNewCart = cart is null;

        // Create cart only when adding the first item
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
            // Prevent integer overflow and exceeding stock
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

        // Save Cart and CartItem together
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateQuantityAsync(
        Guid userId,
        Guid cartItemId,
        UpdateCartItemDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);
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

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveItemAsync(
        Guid userId,
        Guid cartItemId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);

        var item = await GetOwnedItemAsync(
            userId, cartItemId, cancellationToken);

        unitOfWork.GetRepository<CartItem>().Delete(item);

        await unitOfWork.SaveChangesAsync(cancellationToken);
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
                cancellationToken: cancellationToken);

        if (product is null)
            throw new BusinessException(
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
