using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Domain.Enums;

namespace FlowerShop.Application.Interfaces.Queries;

public interface IAdminVoucherQueryService
{
    Task<AdminVoucherPageDto> GetPageAsync(
        Guid adminUserId,
        string? codeKeyword = null,
        DiscountType? discountType = null,
        VoucherAvailabilityStatus? status = null,
        VoucherSortOrder sort = VoucherSortOrder.Newest,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default);

    Task<AdminVoucherViewDto> GetDetailAsync(
        Guid adminUserId,
        Guid voucherId,
        CancellationToken cancellationToken = default);
}
