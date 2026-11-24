namespace FlowerShop.Domain.Enums;

// Derived at read time; not a persisted Voucher property.
public enum VoucherAvailabilityStatus
{
    Inactive = 0,
    Upcoming = 1,
    Available = 2,
    Expired = 3,
    Exhausted = 4
}
