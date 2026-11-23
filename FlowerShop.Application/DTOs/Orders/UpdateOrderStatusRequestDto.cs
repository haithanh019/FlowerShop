using System.ComponentModel.DataAnnotations;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.DTOs.Orders;

public sealed class UpdateOrderStatusRequestDto
{
    [Required]
    [EnumDataType(typeof(OrderStatus))]
    public OrderStatus? Status { get; set; }
}
