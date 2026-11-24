using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Concurrency;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Mappings;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;

namespace FlowerShop.Application.Services;

public sealed class VoucherService(
    IGenericRepository<Voucher> vouchers,
    IVoucherUsageReadRepository usage,
    IUserAccessService userAccessService,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IVoucherService
{
    public async Task<VoucherDto> CreateAsync(
        Guid adminUserId,
        CreateVoucherDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateAdminId(adminUserId);
        Validate(dto);
        var code = VoucherInputPolicy.NormalizeCode(dto.Code)!;
        var terms = NormalizeTerms(dto);
        var isActive = dto.IsActive;

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            VoucherLockKeys.ForCode(code),
            async ct =>
            {
                await EnsureAdminAsync(adminUserId, ct);

                // Baseline audit must reconcile legacy noncanonical codes.
                // The unique database index remains the final uniqueness guard.
                var existing = await vouchers.GetByAsync(
                    x => x.Code == code, cancellationToken: ct);
                if (existing is not null)
                    throw Conflict("VOUCHER_CODE_EXISTS");

                var voucher = new Voucher
                {
                    Code = code,
                    IsActive = isActive,
                    UsedCount = 0,
                    CreatedAt = NowUtc()
                };
                ApplyTerms(voucher, terms);
                await vouchers.AddAsync(voucher, ct);
                return VoucherMapper.ToDto(voucher);
            }, cancellationToken);
    }

    public async Task<VoucherDto> UpdateAsync(
        Guid adminUserId,
        Guid voucherId,
        UpdateVoucherDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateAdminId(adminUserId);
        ValidateVoucherId(voucherId);
        Validate(dto);
        var terms = NormalizeTerms(dto);

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            VoucherLockKeys.ForId(voucherId),
            async ct =>
            {
                await EnsureAdminAsync(adminUserId, ct);
                var voucher = await GetTrackedVoucherAsync(voucherId, ct);

                // Includes cancelled orders: historical commercial terms are frozen.
                var hasHistory = await usage.HasOrderHistoryAsync(voucherId, ct);
                if (hasHistory && CommercialTermsChanged(voucher, terms))
                    throw Conflict("VOUCHER_TERMS_LOCKED");

                // Fail closed on unexplained legacy counters; never repair them here.
                var actualUsed = await usage.CountNonCancelledOrdersAsync(
                    voucherId, cancellationToken: ct);
                if (actualUsed != voucher.UsedCount)
                    throw Conflict("VOUCHER_USAGE_INCONSISTENT");

                if (terms.UsageLimit is { } totalLimit &&
                    totalLimit < voucher.UsedCount)
                    throw Conflict("VOUCHER_USAGE_LIMIT_BELOW_USED_COUNT");

                if (terms.PerUserLimit is { } userLimit &&
                    terms.PerUserLimit != voucher.PerUserLimit &&
                    await usage.HasCustomerUsageAboveLimitAsync(voucherId, userLimit, ct))
                    throw Conflict("VOUCHER_PER_USER_LIMIT_BELOW_USAGE");

                if (!TermsChanged(voucher, terms))
                    return VoucherMapper.ToDto(voucher);

                // Modify tracked editable properties only, not Code, UsedCount or status.
                ApplyTerms(voucher, terms);
                voucher.UpdatedAt = NowUtc();
                return VoucherMapper.ToDto(voucher);
            }, cancellationToken);
    }

    public async Task<VoucherDto> UpdateStatusAsync(
        Guid adminUserId,
        Guid voucherId,
        UpdateVoucherStatusDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateAdminId(adminUserId);
        ValidateVoucherId(voucherId);
        Validate(dto);
        var isActive = dto.IsActive!.Value;

        return await unitOfWork.ExecuteInLockedTransactionAsync(
            VoucherLockKeys.ForId(voucherId),
            async ct =>
            {
                await EnsureAdminAsync(adminUserId, ct);
                var voucher = await GetTrackedVoucherAsync(voucherId, ct);

                if (voucher.IsActive != isActive)
                {
                    voucher.IsActive = isActive;
                    voucher.UpdatedAt = NowUtc();
                }

                // Deactivation remains possible even for inconsistent legacy usage.
                // Activation controls IsActive, not calculated availability.
                return VoucherMapper.ToDto(voucher);
            }, cancellationToken);
    }

    private async Task EnsureAdminAsync(Guid adminUserId, CancellationToken ct)
    {
        if (!await userAccessService.IsActiveWithRoleAsync(
                adminUserId, nameof(UserRole.Admin), ct))
            throw new BusinessException(
                BusinessErrorType.Unauthorized, "ADMIN_ACCESS_REQUIRED");
    }

    private async Task<Voucher> GetTrackedVoucherAsync(Guid voucherId, CancellationToken ct)
        => await vouchers.GetByIdAsync(voucherId, trackChanges: true, cancellationToken: ct)
            ?? throw new BusinessException(BusinessErrorType.NotFound, "VOUCHER_NOT_FOUND");

    private static void ValidateAdminId(Guid adminUserId)
    {
        if (adminUserId == Guid.Empty)
            throw new ValidationException("ADMIN_USER_ID_REQUIRED");
    }

    private static void ValidateVoucherId(Guid voucherId)
    {
        if (voucherId == Guid.Empty)
            throw new ValidationException("VOUCHER_ID_REQUIRED");
    }

    private static void Validate(object dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Validator.ValidateObject(dto, new ValidationContext(dto), validateAllProperties: true);
    }

    private DateTime NowUtc()
        => VoucherInputPolicy.ToUtcMilliseconds(timeProvider.GetUtcNow());

    private static BusinessException Conflict(string code)
        => new(BusinessErrorType.Conflict, code);

    private static Terms NormalizeTerms(VoucherTermsDto dto) => new(
        VoucherInputPolicy.NormalizeDescription(dto.Description),
        dto.DiscountType, dto.DiscountValue, dto.MinOrderAmount,
        dto.MaxDiscountAmount, dto.UsageLimit, dto.PerUserLimit,
        VoucherInputPolicy.ToUtcMilliseconds(dto.StartAt!.Value),
        VoucherInputPolicy.ToUtcMilliseconds(dto.EndAt!.Value));

    private static bool CommercialTermsChanged(Voucher voucher, Terms terms)
        => voucher.DiscountType != terms.DiscountType ||
           voucher.DiscountValue != terms.DiscountValue ||
           voucher.MinOrderAmount != terms.MinOrderAmount ||
           voucher.MaxDiscountAmount != terms.MaxDiscountAmount ||
           voucher.StartAt != terms.StartAt;

    private static bool TermsChanged(Voucher voucher, Terms terms)
        => CommercialTermsChanged(voucher, terms) ||
           voucher.Description != terms.Description ||
           voucher.EndAt != terms.EndAt ||
           voucher.UsageLimit != terms.UsageLimit ||
           voucher.PerUserLimit != terms.PerUserLimit;

    private static void ApplyTerms(Voucher voucher, Terms terms)
    {
        voucher.Description = terms.Description;
        voucher.DiscountType = terms.DiscountType;
        voucher.DiscountValue = terms.DiscountValue;
        voucher.MinOrderAmount = terms.MinOrderAmount;
        voucher.MaxDiscountAmount = terms.MaxDiscountAmount;
        voucher.UsageLimit = terms.UsageLimit;
        voucher.PerUserLimit = terms.PerUserLimit;
        voucher.StartAt = terms.StartAt;
        voucher.EndAt = terms.EndAt;
    }

    private sealed record Terms(
        string? Description,
        DiscountType DiscountType,
        decimal DiscountValue,
        decimal MinOrderAmount,
        decimal? MaxDiscountAmount,
        int? UsageLimit,
        int? PerUserLimit,
        DateTime StartAt,
        DateTime EndAt);
}
