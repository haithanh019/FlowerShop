namespace FlowerShop.Domain.Common;

public sealed class ResourceLockException(int resultCode)
    : Exception("RESOURCE_LOCK_UNAVAILABLE")
{
    public int ResultCode { get; } = resultCode;
}
