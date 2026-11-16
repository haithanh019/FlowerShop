using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class CheckoutService(
    IUnitOfWork unitOfWork,
    IProductStockRepository stockRepository)
    : ICheckoutService
{
    public async Task<CheckoutResultDto> CheckoutAsync(
        Guid userId,
        CheckoutRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");

        ArgumentNullException.ThrowIfNull(dto);

        Validator.ValidateObject(
            dto,
            new ValidationContext(dto),
            validateAllProperties: true);

        var orderCode = $"FS-{dto.CheckoutRequestId:N}";

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:cart:{userId:N}",
            async ct =>
            {
                // 1. Reject an already used CheckoutRequestId
                var existingOrder = await unitOfWork
                    .GetRepository<Order>()
                    .GetByAsync(
                        x => x.OrderCode == orderCode,
                        cancellationToken: ct);



                if (existingOrder is not null)
                {
                    // Never return another user's order.
                    if (existingOrder.UserId != userId)
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "CHECKOUT_REQUEST_ID_IN_USE");

                    // Reconstruct the committed order result.
                    var previousItems = await unitOfWork
                        .GetRepository<OrderItem>()
                        .FindAsync(
                            x => x.OrderId == existingOrder.Id,
                            cancellationToken: ct);

                    var previousPayment = await unitOfWork
                        .GetRepository<Payment>()
                        .GetByAsync(
                            x => x.OrderId == existingOrder.Id &&
                                 x.Method == PaymentMethod.CashOnDelivery,
                            cancellationToken: ct);

                    if (previousPayment is null || previousItems.Count == 0)
                        throw new InvalidOperationException(
                            "CHECKOUT_ORDER_DATA_INCOMPLETE");

                    // FIX: Rename resultItems to previousResultItems
                    var previousResultItems = previousItems
                        .OrderBy(x => x.ProductId)
                        .Select(x => new OrderItemDto(
                            x.ProductId,
                            x.ProductName,
                            x.UnitPrice,
                            x.Quantity,
                            x.LineTotal))
                        .ToArray();

                    // Return existing Order without reducing stock.
                    return new CheckoutResultDto(
                        existingOrder.Id,
                        existingOrder.OrderCode,
                        existingOrder.Status,
                        previousPayment.Method,
                        previousPayment.Status,
                        existingOrder.Subtotal,
                        existingOrder.ShippingFee,
                        existingOrder.DiscountAmount,
                        existingOrder.TotalAmount,
                        previousResultItems,
                        existingOrder.CreatedAt);
                }


                // 2. Verify that the shipping address
                // belongs to the current user
                var address = await unitOfWork
                    .GetRepository<UserAddress>()
                    .GetByAsync(
                        x => x.Id == dto.ShippingAddressId &&
                             x.UserId == userId,
                        cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.NotFound,
                        "SHIPPING_ADDRESS_NOT_FOUND");

                // 3. Find the user's Cart
                var cart = await unitOfWork
                    .GetRepository<Cart>()
                    .GetByAsync(
                        x => x.UserId == userId,
                        cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "CART_EMPTY");

                // 4. Load CartItems with tracking
                var itemRepository =
                    unitOfWork.GetRepository<CartItem>();

                var cartItems = await itemRepository.FindAsync(
                    x => x.CartId == cart.Id,
                    trackChanges: true,
                    cancellationToken: ct);

                if (cartItems.Count == 0)
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "CART_EMPTY");

                if (cartItems.Any(x => x.Quantity <= 0))
                    throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "CART_INVALID_QUANTITY");

                // 5. Load Product information in one query
                var productIds = cartItems
                    .Select(x => x.ProductId)
                    .Distinct()
                    .ToArray();

                var products = await unitOfWork
                    .GetRepository<Product>()
                    .FindAsync(
                        x => productIds.Contains(x.Id),
                        cancellationToken: ct);

                var productLookup = products.ToDictionary(
                    x => x.Id);

                // 6. Validate products and build snapshots
                var orderItems = new List<OrderItem>();

                foreach (var cartItem in cartItems.OrderBy(x => x.ProductId))
                {
                    if (!productLookup.TryGetValue(
                        cartItem.ProductId,
                        out var product))
                    {
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "CART_PRODUCT_UNAVAILABLE");
                    }

                    if (!product.IsActive)
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "PRODUCT_INACTIVE");

                    if (product.StockQuantity < cartItem.Quantity)
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "INSUFFICIENT_STOCK");

                    orderItems.Add(new OrderItem
                    {
                        ProductId = product.Id,
                        ProductName = product.Name,
                        UnitPrice = product.Price,
                        Quantity = cartItem.Quantity
                    });
                }

                // 7. Calculate amounts on the server
                var subtotal = orderItems.Sum(
                    x => x.UnitPrice * x.Quantity);

                // Temporary MVP pricing policy:
                // no shipping fee or voucher discount yet
                const decimal shippingFee = 0m;
                const decimal discountAmount = 0m;

                var totalAmount =
                    subtotal + shippingFee - discountAmount;

                // 8. Decrease stock atomically
                // Consistent ordering reduces deadlock risk
                foreach (var item in orderItems
                    .OrderBy(x => x.ProductId))
                {
                    var decreased =
                        await stockRepository.TryDecreaseAsync(
                            item.ProductId,
                            item.Quantity,
                            ct);

                    if (!decreased)
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "INSUFFICIENT_STOCK");
                }

                // 9. Create the Order with address snapshot
                var order = new Order
                {
                    UserId = userId,
                    OrderCode = orderCode,
                    Status = OrderStatus.Pending,

                    RecipientName = address.RecipientName,
                    RecipientPhone = address.RecipientPhone,

                    ShippingAddressLine = address.AddressLine,
                    ShippingWard = address.Ward,
                    ShippingDistrict = address.District,
                    ShippingProvince = address.Province,
                    ShippingCountryCode = address.CountryCode,
                    ShippingPostalCode = address.PostalCode,

                    Note = dto.Note,

                    Subtotal = subtotal,
                    ShippingFee = shippingFee,
                    DiscountAmount = discountAmount,
                    TotalAmount = totalAmount,

                    Items = orderItems,

                    Payments =
                    [
                        new Payment
                        {
                            Method = PaymentMethod.CashOnDelivery,
                            Status = PaymentStatus.Pending,
                            Amount = totalAmount,
                            Currency = "VND"
                        }
                    ]
                };

                await unitOfWork.GetRepository<Order>()
                    .AddAsync(order, ct);

                // 10. Clear checked-out CartItems
                foreach (var cartItem in cartItems)
                {
                    itemRepository.Delete(cartItem);
                }

                // UnitOfWork saves and commits
                // after this delegate completes successfully

                // 11. Prepare the response
                var resultItems = orderItems
                    .Select(x => new OrderItemDto(
                        x.ProductId,
                        x.ProductName,
                        x.UnitPrice,
                        x.Quantity,
                        x.LineTotal))
                    .ToArray();

                return new CheckoutResultDto(
                    order.Id,
                    order.OrderCode,
                    order.Status,
                    PaymentMethod.CashOnDelivery,
                    PaymentStatus.Pending,
                    order.Subtotal,
                    order.ShippingFee,
                    order.DiscountAmount,
                    order.TotalAmount,
                    resultItems,
                    order.CreatedAt);
            },
            cancellationToken);
    }
}
