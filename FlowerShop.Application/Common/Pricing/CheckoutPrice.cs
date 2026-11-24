namespace FlowerShop.Application.Common.Pricing;

// Prepared within the current transaction, not a committed redemption.
// ExpectedUsedCount is null for no-voucher checkout and fresh under the voucher lock otherwise.
public readonly record struct CheckoutPrice(
    Guid? VoucherId, VoucherPrice Price, int? ExpectedUsedCount);
