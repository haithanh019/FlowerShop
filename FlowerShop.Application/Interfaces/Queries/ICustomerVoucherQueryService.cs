using FlowerShop.Application.DTOs.Vouchers;

namespace FlowerShop.Application.Interfaces.Queries;

public interface ICustomerVoucherQueryService
{
    Task<CustomerVoucherPageDto> GetAvailableAsync(
        Guid userId, int page = 1, int pageSize = 10,
        CancellationToken cancellationToken = default);
}
