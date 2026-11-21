using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Application.Common.Validation;

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
        var fingerprint = CheckoutRequestFingerprint.Compute(dto);

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:cart:{userId:N}",
            async ct =>
            {
                // 1. Replays must match the original, committed payload.
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

                    // Legacy Orders do not contain sufficient request data
                    // to safely distinguish genuine replays from mismatches.
                    if (existingOrder.CheckoutRequestFingerprint is null)
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "CHECKOUT_REQUEST_NOT_VERIFIABLE");

                    if (!StringComparer.Ordinal.Equals(
                        existingOrder.CheckoutRequestFingerprint,
                        fingerprint))
                    {
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "CHECKOUT_REQUEST_MISMATCH");
                    }

                    // Reconstruct the committed order result.
                    var previousItems = await unitOfWork
                        .GetRepository<OrderItem>()
                        .FindAsync(
                            x => x.OrderId == existingOrder.Id,
                            cancellationToken: ct);


                    var previousPayments = await unitOfWork
                        .GetRepository<Payment>()
                        .FindAsync(
                            x => x.OrderId == existingOrder.Id,
                            cancellationToken: ct);

                    if (!OrderFinancialConsistency.HasValidCodPayment(
                            existingOrder, previousPayments) ||
                        !OrderFinancialConsistency.HasValidOrderItems(
                            existingOrder, previousItems))
                    {
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "CHECKOUT_ORDER_DATA_INCOMPLETE");
                    }

                    var previousPayment = previousPayments[0];

                    var previousResultItems = previousItems
                        .OrderBy(x => x.ProductId)
                        .Select(x => new OrderItemDto(
                            x.ProductId,
                            x.ProductName,
                            x.UnitPrice,
                            x.Quantity,
                            x.LineTotal))
                        .ToArray();

                    // Do not reduce stock on replay.
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
                        existingOrder.CreatedAt,
                        IsReplay: true);
                }

                // 2. Verify address ownership.
                var address = await unitOfWork
                    .GetRepository<UserAddress>()
                    .GetByAsync(
                        x => x.Id == dto.ShippingAddressId &&
                             x.UserId == userId,
                        cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.NotFound,
                        "SHIPPING_ADDRESS_NOT_FOUND");

                // 3. Find the user's Cart.
                var cart = await unitOfWork
                    .GetRepository<Cart>()
                    .GetByAsync(
                        x => x.UserId == userId,
                        cancellationToken: ct) ?? throw new BusinessException(
                        BusinessErrorType.Conflict,
                        "CART_EMPTY");

                // 4. Load CartItems with tracking.
                var itemRepository = unitOfWork.GetRepository<CartItem>();
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

                // 5. Load Product information in one query.
                var productIds = cartItems
                    .Select(x => x.ProductId)
                    .Distinct()
                    .ToArray();

                var products = await unitOfWork
                    .GetRepository<Product>()
                    .FindAsync(
                        x => productIds.Contains(x.Id),
                        cancellationToken: ct);

                var productLookup = products.ToDictionary(x => x.Id);

                // 6. Validate products and build immutable snapshots.
                var orderItems = new List<OrderItem>();
                foreach (var cartItem in cartItems.OrderBy(x => x.ProductId))
                {
                    if (!productLookup.TryGetValue(
                        cartItem.ProductId, out var product))
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

                // 7. Pricing policy for COD MVP.
                var subtotal = orderItems.Sum(
                    x => x.UnitPrice * x.Quantity);
                const decimal shippingFee = 0m;
                const decimal discountAmount = 0m;
                var totalAmount = subtotal + shippingFee - discountAmount;

                // 8. Decrease stock atomically in a consistent order.
                foreach (var item in orderItems.OrderBy(x => x.ProductId))
                {
                    var decreased = await stockRepository.TryDecreaseAsync(
                        item.ProductId, item.Quantity, ct);
                    if (!decreased)
                        throw new BusinessException(
                            BusinessErrorType.Conflict,
                            "INSUFFICIENT_STOCK");
                }

                // 9. Persist the request fingerprint with the Order.
                var order = new Order
                {
                    UserId = userId,
                    OrderCode = orderCode,
                    CheckoutRequestFingerprint = fingerprint,
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

                // 10. Clear checked-out CartItems.
                foreach (var cartItem in cartItems)
                    itemRepository.Delete(cartItem);

                // UnitOfWork saves all tracked entities and commits.
                // 11. Build result from saved snapshots.
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
