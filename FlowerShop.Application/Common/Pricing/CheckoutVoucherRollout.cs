namespace FlowerShop.Application.Common.Pricing;

// Temporary rollout gate. Production DI always registers false in step D.
// Integration tests can construct CheckoutService with true explicitly.
// Do not enable normal checkout until cancellation restoration is complete.
public sealed record CheckoutVoucherRollout(bool Enabled);
