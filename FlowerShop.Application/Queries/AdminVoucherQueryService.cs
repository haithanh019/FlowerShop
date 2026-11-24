using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Queries;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Mappings;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Domain.Policies;

namespace FlowerShop.Application.Queries;

public sealed class AdminVoucherQueryService(
    IAdminVoucherReadRepository vouchers,
    IUserAccessService userAccessService,
    TimeProvider timeProvider) : IAdminVoucherQueryService
{
    public async Task<AdminVoucherPageDto> GetPageAsync(
        Guid adminUserId,
        string? codeKeyword = null,
        DiscountType? discountType = null,
        VoucherAvailabilityStatus? status = null,
        VoucherSortOrder sort = VoucherSortOrder.Newest,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(adminUserId, cancellationToken);

        if (page < 1)
            throw new ValidationException("PAGE_MUST_BE_POSITIVE");
        if (pageSize is < 1 or > 50)
            throw new ValidationException("PAGE_SIZE_OUT_OF_RANGE");
        if (((long)page - 1) * pageSize > int.MaxValue)
            throw new ValidationException("PAGE_OUT_OF_RANGE");
        if (discountType.HasValue && !Enum.IsDefined(discountType.Value))
            throw new ValidationException("INVALID_DISCOUNT_TYPE");
        if (status.HasValue && !Enum.IsDefined(status.Value))
            throw new ValidationException("INVALID_VOUCHER_STATUS");
        if (!Enum.IsDefined(sort))
            throw new ValidationException("INVALID_VOUCHER_SORT");

        var keywordError = VoucherInputPolicy.ValidateCode(
            codeKeyword, nameof(codeKeyword), required: false).FirstOrDefault();
        if (keywordError is not null)
            throw new ValidationException(keywordError.ErrorMessage);
        var keyword = VoucherInputPolicy.NormalizeCode(codeKeyword);

        var now = VoucherInputPolicy.ToUtcMilliseconds(timeProvider.GetUtcNow());
        var (rows, totalCount) = await vouchers.GetPageAsync(
            keyword, discountType, status, sort, now, page, pageSize,
            cancellationToken);

        // Compile once per request; reuse the SQL policy for every returned row.
        var classify = VoucherAvailabilityPolicy.StatusExpression(now).Compile();
        var items = rows.Select(x => new AdminVoucherViewDto(
            VoucherMapper.ToDto(x), classify(x), now)).ToArray();
        var totalPages = totalCount == 0 ? 0 : 1 + (totalCount - 1) / pageSize;

        return new AdminVoucherPageDto(items, page, pageSize,
            totalCount, totalPages, now);
    }

    public async Task<AdminVoucherViewDto> GetDetailAsync(
        Guid adminUserId,
        Guid voucherId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(adminUserId, cancellationToken);
        if (voucherId == Guid.Empty)
            throw new ValidationException("VOUCHER_ID_REQUIRED");

        var now = VoucherInputPolicy.ToUtcMilliseconds(timeProvider.GetUtcNow());
        var voucher = await vouchers.GetByIdAsync(voucherId, cancellationToken)
            ?? throw new BusinessException(BusinessErrorType.NotFound, "VOUCHER_NOT_FOUND");

        var classify = VoucherAvailabilityPolicy.StatusExpression(now).Compile();
        return new AdminVoucherViewDto(VoucherMapper.ToDto(voucher), classify(voucher), now);
    }

    private async Task EnsureAdminAsync(Guid adminUserId, CancellationToken cancellationToken)
    {
        if (adminUserId == Guid.Empty)
            throw new ValidationException("ADMIN_USER_ID_REQUIRED");
        if (!await userAccessService.IsActiveWithRoleAsync(
                adminUserId, nameof(UserRole.Admin), cancellationToken))
            throw new BusinessException(BusinessErrorType.Unauthorized, "ADMIN_ACCESS_REQUIRED");
    }
}
