using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Queries;

public sealed class CustomerVoucherQueryService(
    ICustomerVoucherReadRepository vouchers,
    IUserAccessService userAccessService,
    TimeProvider timeProvider) : ICustomerVoucherQueryService
{
    public async Task<CustomerVoucherPageDto> GetAvailableAsync(
        Guid userId, int page = 1, int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ValidationException("INVALID_USER_ID");
        if (!await userAccessService.IsActiveWithRoleAsync(
                userId, nameof(UserRole.Customer), cancellationToken))
            throw new BusinessException(BusinessErrorType.Unauthorized, "CUSTOMER_ACCESS_REQUIRED");
        if (page < 1)
            throw new ValidationException("PAGE_MUST_BE_POSITIVE");
        if (pageSize is < 1 or > 50)
            throw new ValidationException("PAGE_SIZE_OUT_OF_RANGE");
        if (((long)page - 1) * pageSize > int.MaxValue)
            throw new ValidationException("PAGE_OUT_OF_RANGE");

        var now = VoucherInputPolicy.ToUtcMilliseconds(timeProvider.GetUtcNow());
        var (rows, totalCount) = await vouchers.GetAvailablePageAsync(
            userId, now, page, pageSize, cancellationToken);
        var items = rows.Select(x => new CustomerVoucherDto(
            x.Code, x.Description, x.DiscountType, x.DiscountValue,
            x.MinOrderAmount, x.MaxDiscountAmount, x.PerUserLimit,
            DateTime.SpecifyKind(x.StartAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(x.EndAt, DateTimeKind.Utc))).ToArray();
        var totalPages = totalCount == 0 ? 0 : 1 + (totalCount - 1) / pageSize;
        return new CustomerVoucherPageDto(items, page, pageSize, totalCount, totalPages, now);
    }
}
