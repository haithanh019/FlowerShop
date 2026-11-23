
namespace FlowerShop.Application.DTOs.Orders;

public sealed record AdminOrderDetailDto(
    Guid CustomerUserId,
    string CustomerEmail,
    OrderDetailDto Order);
