using FlowerShop.Application.DTOs.Carts;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class CartQueryService(
    IGenericRepository<Cart> carts,
    IGenericRepository<CartItem> cartItems,
    IGenericRepository<Product> products,
    IGenericRepository<ProductImage> productImages)
    : ICartQueryService
{
    public async Task<CartDto> GetByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Find the cart belonging to this user
        var cart = await carts.GetByAsync(
            x => x.UserId == userId,
            cancellationToken: cancellationToken);

        // Reading a cart must not create a new one
        if (cart is null)
            return new CartDto(null, [], 0, 0m);

        // Load cart items without tracking
        var items = await cartItems.FindAsync(
            x => x.CartId == cart.Id,
            cancellationToken: cancellationToken);

        if (items.Count == 0)
            return new CartDto(cart.Id, [], 0, 0m);

        // Collect product IDs to avoid N+1 queries
        var productIds = items
            .Select(x => x.ProductId)
            .Distinct()
            .ToArray();

        // Load products in one query
        var productList = await products.FindAsync(
            x => productIds.Contains(x.Id),
            cancellationToken: cancellationToken);

        var productLookup = productList.ToDictionary(x => x.Id);

        // Load primary images in one query
        var images = await productImages.FindAsync(
            x => productIds.Contains(x.ProductId) && x.IsPrimary,
            cancellationToken: cancellationToken);

        var imageLookup = images.ToDictionary(
            x => x.ProductId,
            x => x.ImageUrl);

        // Map entities to DTOs
        var resultItems = items
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(item =>
            {
                var product = productLookup[item.ProductId];

                imageLookup.TryGetValue(
                    item.ProductId,
                    out var imageUrl);

                var isAvailable =
                    product.IsActive &&
                    product.StockQuantity >= item.Quantity;

                return new CartItemDto(
                    item.Id,
                    item.ProductId,
                    product.Name,
                    product.Slug,
                    imageUrl,
                    product.Price,
                    item.Quantity,
                    product.Price * item.Quantity,
                    isAvailable);
            })
            .ToArray();

        // Sum all quantities, including unavailable items
        var totalQuantity = resultItems.Sum(x => x.Quantity);

        // Calculate subtotal for available items only
        var subtotal = resultItems
            .Where(x => x.IsAvailable)
            .Sum(x => x.LineTotal);

        return new CartDto(
            cart.Id,
            resultItems,
            totalQuantity,
            subtotal);
    }
}
