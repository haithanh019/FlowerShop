namespace FlowerShop.Application.Common.Pricing;

// A prepared price, not a reservation or a committed redemption.
public readonly record struct CheckoutPrice(Guid? VoucherId, VoucherPrice Price);
