namespace FlowerShop.Domain.Enums;

public enum OrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Preparing = 2,
    Shipping = 3,
    Delivered = 4,
    Cancelled = 5
}
