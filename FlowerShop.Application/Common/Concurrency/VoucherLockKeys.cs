namespace FlowerShop.Application.Common.Concurrency;

public static class VoucherLockKeys
{
    // All future redemption, release and administration paths must use this key.
    public static string ForId(Guid voucherId)
        => $"flower-shop:voucher:{voucherId:N}";

    // Only use a validated, normalized, immutable voucher code.
    public static string ForCode(string normalizedCode)
        => $"flower-shop:voucher-code:{normalizedCode}";
}
