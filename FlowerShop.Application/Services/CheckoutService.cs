using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class CheckoutService(
    IUnitOfWork unitOfWork,
    IProductStockRepository stockRepository,
    ICheckoutPricingService pricingService)
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
        Validator.ValidateObject(dto, new ValidationContext(dto), validateAllProperties: true);

        var orderCode = $"FS-{dto.CheckoutRequestId:N}";
        var fingerprint = CheckoutRequestFingerprint.Compute(dto);
        var voucherCode = VoucherInputPolicy.NormalizeCode(dto.VoucherCode);

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            $"flower-shop:cart:{userId:N}",
            async ct =>
            {
                // 1. Replay the committed payload, before checking current cart or pricing.
                var existingOrder = await unitOfWork.GetRepository<Order>().GetByAsync(
                    x => x.OrderCode == orderCode, cancellationToken: ct);

                if (existingOrder is not null)
                {
                    if (existingOrder.UserId != userId)
                        throw new BusinessException(BusinessErrorType.Conflict, "CHECKOUT_REQUEST_ID_IN_USE");
                    if (existingOrder.CheckoutRequestFingerprint is null)
                        throw new BusinessException(BusinessErrorType.Conflict, "CHECKOUT_REQUEST_NOT_VERIFIABLE");
                    if (!StringComparer.Ordinal.Equals(existingOrder.CheckoutRequestFingerprint, fingerprint))
                        throw new BusinessException(BusinessErrorType.Conflict, "CHECKOUT_REQUEST_MISMATCH");

                    // Detect incomplete v2 orders potentially created before the rollout guard.
                    // Do not silently replay a voucher request at undiscounted pricing.
                    if (voucherCode is not null &&
                        (existingOrder.VoucherId is null || existingOrder.DiscountAmount <= 0m))
                        throw new BusinessException(BusinessErrorType.Conflict, "CHECKOUT_ORDER_DATA_INCOMPLETE");

                    var previousItems = await unitOfWork.GetRepository<OrderItem>().FindAsync(
                        x => x.OrderId == existingOrder.Id, cancellationToken: ct);
                    var previousPayments = await unitOfWork.GetRepository<Payment>().FindAsync(
                        x => x.OrderId == existingOrder.Id, cancellationToken: ct);

                    if (!OrderFinancialConsistency.HasValidCodPayment(existingOrder, previousPayments) ||
                        !OrderFinancialConsistency.HasValidOrderItems(existingOrder, previousItems))
                        throw new BusinessException(BusinessErrorType.Conflict, "CHECKOUT_ORDER_DATA_INCOMPLETE");

                    var previousPayment = previousPayments[0];
                    var previousResultItems = previousItems.OrderBy(x => x.ProductId)
                        .Select(x => new OrderItemDto(x.ProductId, x.ProductName,
                            x.UnitPrice, x.Quantity, x.LineTotal)).ToArray();

                    return new CheckoutResultDto(existingOrder.Id, existingOrder.OrderCode,
                        existingOrder.Status, previousPayment.Method, previousPayment.Status,
                        existingOrder.Subtotal, existingOrder.ShippingFee, existingOrder.DiscountAmount,
                        existingOrder.TotalAmount, previousResultItems, existingOrder.CreatedAt, IsReplay: true);
                }

                // Temporary rollout guard. Remove only after atomic redemption AND
                // cancellation restoration are implemented and verified.
                if (voucherCode is not null)
                    throw new BusinessException(BusinessErrorType.Conflict, "CHECKOUT_VOUCHER_NOT_ENABLED");

                // 2. Verify address ownership.
                var address = await unitOfWork.GetRepository<UserAddress>().GetByAsync(
                    x => x.Id == dto.ShippingAddressId && x.UserId == userId, cancellationToken: ct)
                    ?? throw new BusinessException(BusinessErrorType.NotFound, "SHIPPING_ADDRESS_NOT_FOUND");

                // 3. Find the user's cart.
                var cart = await unitOfWork.GetRepository<Cart>().GetByAsync(
                    x => x.UserId == userId, cancellationToken: ct)
                    ?? throw new BusinessException(BusinessErrorType.Conflict, "CART_EMPTY");

                // 4. Load CartItems with tracking.
                var itemRepository = unitOfWork.GetRepository<CartItem>();
                var cartItems = await itemRepository.FindAsync(x => x.CartId == cart.Id,
                    trackChanges: true, cancellationToken: ct);
                if (cartItems.Count == 0)
                    throw new BusinessException(BusinessErrorType.Conflict, "CART_EMPTY");
                if (cartItems.Any(x => x.Quantity <= 0))
                    throw new BusinessException(BusinessErrorType.Conflict, "CART_INVALID_QUANTITY");

                // 5. Load products in one query.
                var productIds = cartItems.Select(x => x.ProductId).Distinct().ToArray();
                var products = await unitOfWork.GetRepository<Product>().FindAsync(
                    x => productIds.Contains(x.Id), cancellationToken: ct);
                var productLookup = products.ToDictionary(x => x.Id);

                // 6. Build immutable order item snapshots.
                var orderItems = new List<OrderItem>();
                foreach (var cartItem in cartItems.OrderBy(x => x.ProductId))
                {
                    if (!productLookup.TryGetValue(cartItem.ProductId, out var product))
                        throw new BusinessException(BusinessErrorType.Conflict, "CART_PRODUCT_UNAVAILABLE");
                    if (!product.IsActive)
                        throw new BusinessException(BusinessErrorType.Conflict, "PRODUCT_INACTIVE");
                    if (product.StockQuantity < cartItem.Quantity)
                        throw new BusinessException(BusinessErrorType.Conflict, "INSUFFICIENT_STOCK");

                    orderItems.Add(new OrderItem
                    {
                        ProductId = product.Id, ProductName = product.Name,
                        UnitPrice = product.Price, Quantity = cartItem.Quantity
                    });
                }

                // 7. Shared pricing. Only the no-voucher path is enabled in step C.
                var subtotal = CheckoutPricingPolicy.CalculateSubtotal(
                    orderItems.Select(x => (x.UnitPrice, x.Quantity)));
                var prepared = await pricingService.PrepareAsync(userId, voucherCode, subtotal, ct);
                // Defense in depth: never persist a discounted order without usage accounting.
                if (prepared.VoucherId is not null || prepared.Price.DiscountAmount != 0m)
                    throw new BusinessException(BusinessErrorType.Conflict, "CHECKOUT_VOUCHER_NOT_ENABLED");
                var price = prepared.Price;

                // 8. Decrease stock atomically in a consistent order.
                foreach (var item in orderItems.OrderBy(x => x.ProductId))
                {
                    if (!await stockRepository.TryDecreaseAsync(item.ProductId, item.Quantity, ct))
                        throw new BusinessException(BusinessErrorType.Conflict, "INSUFFICIENT_STOCK");
                }

                // 9. Persist the order and payment in the same transaction.
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
                    Subtotal = price.Subtotal,
                    ShippingFee = price.ShippingFee,
                    DiscountAmount = price.DiscountAmount,
                    TotalAmount = price.TotalAmount,
                    Items = orderItems,
                    Payments =
                    [
                        new Payment
                        {
                            Method = PaymentMethod.CashOnDelivery,
                            Status = PaymentStatus.Pending,
                            Amount = price.TotalAmount,
                            Currency = "VND"
                        }
                    ]
                };
                await unitOfWork.GetRepository<Order>().AddAsync(order, ct);

                // 10. Clear checked-out CartItems.
                foreach (var cartItem in cartItems)
                    itemRepository.Delete(cartItem);

                // 11. UnitOfWork saves and commits; result uses the order snapshots.
                var resultItems = orderItems.Select(x => new OrderItemDto(x.ProductId,
                    x.ProductName, x.UnitPrice, x.Quantity, x.LineTotal)).ToArray();
                return new CheckoutResultDto(order.Id, order.OrderCode, order.Status,
                    PaymentMethod.CashOnDelivery, PaymentStatus.Pending, order.Subtotal,
                    order.ShippingFee, order.DiscountAmount, order.TotalAmount, resultItems, order.CreatedAt);
            }, cancellationToken);
    }
}
