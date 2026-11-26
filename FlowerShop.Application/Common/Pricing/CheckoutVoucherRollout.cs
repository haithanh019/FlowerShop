namespace FlowerShop.Application.Common.Pricing;

// Controls new voucher redemption only. Replay and cancellation must remain available.
public sealed record CheckoutVoucherRollout(bool Enabled);
