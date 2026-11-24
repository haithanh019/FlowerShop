using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Concurrency;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class CheckoutPricingService(
    IUnitOfWork unitOfWork,
    IVoucherUsageReadRepository usage,
    IUserAccessService userAccessService,
    TimeProvider timeProvider) : ICheckoutPricingService
{
    public async Task<CheckoutPrice> PrepareAsync(
        Guid userId, string? voucherCode, decimal subtotal,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");

        var error = VoucherInputPolicy.ValidateCode(voucherCode,
            "VoucherCode", required: false).FirstOrDefault();
        if (error is not null)
            throw new ValidationException(error, null, voucherCode);

        cancellationToken.ThrowIfCancellationRequested();
        var code = VoucherInputPolicy.NormalizeCode(voucherCode);
        const decimal shipping = CheckoutPricingPolicy.CodShippingFee;

        if (code is null)
        {
            var total = CheckoutPricingPolicy.CalculateTotal(subtotal, shipping, 0m);
            return new CheckoutPrice(null, new VoucherPrice(subtotal, shipping, 0m, total));
        }

        if (!CheckoutPricingPolicy.IsStoredMoney(subtotal) || subtotal <= 0m)
            throw Conflict("CHECKOUT_TOTAL_OUT_OF_RANGE");
        if (!await userAccessService.IsActiveWithRoleAsync(
                userId, nameof(UserRole.Customer), cancellationToken))
            throw new BusinessException(BusinessErrorType.Unauthorized, "CUSTOMER_ACCESS_REQUIRED");

        // Resolve only the immutable identity before locking. Do not trust pricing
        // or counters from this initial read. Both reads are no-tracking.
        var repository = unitOfWork.GetRepository<Voucher>();
        var candidate = await repository.GetByAsync(x => x.Code == code,
            cancellationToken: cancellationToken)
            ?? throw NotFound();
        EnsureCanonical(candidate, code);

        // Does not start or commit a transaction. Failure must escape the caller's
        // transaction callback. Cart -> Voucher -> Product stock is the lock order.
        await unitOfWork.AcquireTransactionLockAsync(
            VoucherLockKeys.ForId(candidate.Id), cancellationToken);

        var voucher = await repository.GetByIdAsync(candidate.Id,
            trackChanges: false, cancellationToken: cancellationToken)
            ?? throw NotFound();
        EnsureCanonical(voucher, code);

        var actualUsed = await usage.CountNonCancelledOrdersAsync(
            voucher.Id, cancellationToken: cancellationToken);
        var customerUsed = await usage.CountNonCancelledOrdersAsync(
            voucher.Id, userId, cancellationToken);
        var now = VoucherInputPolicy.ToUtcMilliseconds(timeProvider.GetUtcNow());
        var price = VoucherPricingPolicy.Evaluate(voucher, subtotal, shipping,
            actualUsed, customerUsed, now);

        // No mutation here. Step D will persist VoucherId and increment UsedCount
        // under this same lock before the outer transaction commits.
        return new CheckoutPrice(voucher.Id, price);
    }

    private static void EnsureCanonical(Voucher voucher, string code)
    {
        if (!StringComparer.Ordinal.Equals(voucher.Code, code))
            throw Conflict("VOUCHER_CODE_NOT_CANONICAL");
    }

    private static BusinessException NotFound() =>
        new(BusinessErrorType.NotFound, "VOUCHER_NOT_FOUND");

    private static BusinessException Conflict(string code) =>
        new(BusinessErrorType.Conflict, code);
}
