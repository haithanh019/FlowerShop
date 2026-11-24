using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class VoucherServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 12, 1, 7, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public Task Create_ShouldNormalizeRejectDuplicateAndPersistExplicitInactive()
        => WithDatabaseAsync(async s =>
        {
            var dto = NewCreate($"  {s.Tag.ToLowerInvariant()}-a  ");
            dto.Description = "  Flower promotion  ";
            dto.IsActive = false;
            // Exercise UTC conversion and millisecond truncation.
            dto.StartAt = Start.AddTicks(1234);
            var created = await s.CallAsync(service => service.CreateAsync(s.Admin.Id, dto));
            var stored = await s.ReadAsync(created.VoucherId);
            Assert.Equal(s.Tag + "-A", stored.Code);
            Assert.Equal("Flower promotion", stored.Description);
            Assert.False(stored.IsActive);
            Assert.False(created.IsActive);
            Assert.Equal(0, stored.UsedCount);
            Assert.Equal(Start.UtcDateTime, stored.StartAt);
            Assert.Equal(DateTimeKind.Utc, created.StartAtUtc.Kind);
            Assert.Equal(created.StartAtUtc, stored.StartAt);
            Assert.Equal(created.CreatedAtUtc, stored.CreatedAt);
            Assert.Null(stored.UpdatedAt);

            await ExpectBusinessAsync(BusinessErrorType.Conflict, "VOUCHER_CODE_EXISTS",
                () => s.CallAsync(service => service.CreateAsync(s.Admin.Id,
                    NewCreate(s.Tag + "-A"))));

            var defaultActive = await s.CallAsync(service => service.CreateAsync(s.Admin.Id,
                NewCreate(s.Tag + "-B")));
            Assert.True((await s.ReadAsync(defaultActive.VoucherId)).IsActive);
        });

    [Fact]
    public Task Update_ShouldAllowUnusedTermsAndStatusAndPreserveNoOpTimestamp()
        => WithDatabaseAsync(async s =>
        {
            var created = await s.CreateAsync("-A");
            var update = ToUpdate(created);
            update.DiscountType = DiscountType.FixedAmount;
            update.DiscountValue = 25000m;
            update.MaxDiscountAmount = null;
            update.MinOrderAmount = 150000m;
            update.StartAt = Start.AddDays(1);
            update.EndAt = Start.AddDays(5);
            update.UsageLimit = 20;
            update.PerUserLimit = 2;
            update.Description = "  Updated terms  ";

            var changed = await s.CallAsync(service => service.UpdateAsync(s.Admin.Id,
                created.VoucherId, update));
            Assert.Equal(created.Code, changed.Code);
            Assert.Equal(created.CreatedAtUtc, changed.CreatedAtUtc);
            Assert.Equal(0, changed.UsedCount);
            Assert.True(changed.IsActive);
            Assert.Equal(DiscountType.FixedAmount, changed.DiscountType);
            Assert.Equal(25000m, changed.DiscountValue);
            Assert.Equal(150000m, changed.MinOrderAmount);
            Assert.Null(changed.MaxDiscountAmount);
            Assert.Equal(Start.AddDays(1).UtcDateTime, changed.StartAtUtc);
            Assert.Equal("Updated terms", changed.Description);
            Assert.Equal<int?>(20, changed.UsageLimit);
            Assert.Equal<int?>(2, changed.PerUserLimit);
            Assert.NotNull(changed.UpdatedAtUtc);
            Assert.Equal<DateTimeKind?>(DateTimeKind.Utc, changed.UpdatedAtUtc?.Kind);
            Assert.Equal(changed.UpdatedAtUtc, (await s.ReadAsync(created.VoucherId)).UpdatedAt);

            // Different offset representation, same instant; whitespace-only difference.
            var repeat = ToUpdate(changed);
            repeat.Description = "  Updated terms  ";
            repeat.StartAt = Start.AddDays(1).ToOffset(TimeSpan.FromHours(8));
            Assert.Equal(changed, await s.CallAsync(service => service.UpdateAsync(
                s.Admin.Id, created.VoucherId, repeat)));

            var inactive = await s.StatusAsync(created.VoucherId, false);
            Assert.False(inactive.IsActive);
            Assert.False((await s.ReadAsync(created.VoucherId)).IsActive);
            Assert.Equal(changed.DiscountValue, inactive.DiscountValue);
            Assert.Equal(inactive, await s.StatusAsync(created.VoucherId, false));
            Assert.True((await s.StatusAsync(created.VoucherId, true)).IsActive);
            Assert.True((await s.ReadAsync(created.VoucherId)).IsActive);

            var clear = ToUpdate(changed);
            clear.Description = "   ";
            clear.UsageLimit = null;
            clear.PerUserLimit = null;
            var cleared = await s.CallAsync(service => service.UpdateAsync(s.Admin.Id,
                created.VoucherId, clear));
            Assert.Null(cleared.Description);
            Assert.Null(cleared.UsageLimit);
            Assert.Null(cleared.PerUserLimit);
        });

    [Fact]
    public Task Update_ShouldFreezeEveryCommercialTermEvenAfterAllOrdersCancelled()
        => WithDatabaseAsync(async s =>
        {
            var created = await s.CreateAsync("-A");
            // Cancelled history is enough to freeze terms, even when UsedCount is zero.
            await s.AddOrderAsync(created.VoucherId, s.Customer.Id, OrderStatus.Cancelled);
            Action<UpdateVoucherDto>[] changes =
            [
                dto => { dto.DiscountType = DiscountType.FixedAmount; dto.MaxDiscountAmount = null; },
                dto => dto.DiscountValue = 30m,
                dto => dto.MinOrderAmount = 90000m,
                dto => dto.MaxDiscountAmount = 60000m,
                dto => dto.StartAt = Start.AddHours(1)
            ];

            foreach (var change in changes)
            {
                var dto = ToUpdate(created);
                change(dto);
                await ExpectBusinessAsync(BusinessErrorType.Conflict, "VOUCHER_TERMS_LOCKED",
                    () => s.CallAsync(service => service.UpdateAsync(s.Admin.Id,
                        created.VoucherId, dto)));
            }

            var before = await s.ReadAsync(created.VoucherId);
            Assert.Equal(created.DiscountType, before.DiscountType);
            Assert.Equal(created.DiscountValue, before.DiscountValue);
            Assert.Equal(created.MinOrderAmount, before.MinOrderAmount);
            Assert.Equal(created.MaxDiscountAmount, before.MaxDiscountAmount);
            Assert.Equal(created.StartAtUtc, before.StartAt);
            Assert.Null(before.UpdatedAt);

            var allowed = ToUpdate(created);
            allowed.Description = "Extended promotion";
            allowed.EndAt = Start.AddDays(10);
            allowed.UsageLimit = 1;
            allowed.PerUserLimit = 1;
            var result = await s.CallAsync(service => service.UpdateAsync(s.Admin.Id,
                created.VoucherId, allowed));
            Assert.Equal("Extended promotion", result.Description);
            Assert.Equal(Start.AddDays(10).UtcDateTime, result.EndAtUtc);
            Assert.Equal(0, result.UsedCount);
            Assert.Equal(OrderStatus.Cancelled, await s.Db.Orders.AsNoTracking()
                .Where(x => x.VoucherId == created.VoucherId)
                .Select(x => x.Status).SingleAsync());
        });

    [Fact]
    public Task Update_ShouldEnforceGlobalAndPerUserLimitsUsingNonCancelledOrders()
        => WithDatabaseAsync(async s =>
        {
            var created = await s.CreateAsync("-A");
            await s.AddOrderAsync(created.VoucherId, s.Customer.Id, OrderStatus.Pending);
            await s.AddOrderAsync(created.VoucherId, s.Customer.Id, OrderStatus.Delivered);
            await s.AddOrderAsync(created.VoucherId, s.Other.Id, OrderStatus.Confirmed);
            await s.AddOrderAsync(created.VoucherId, s.Customer.Id, OrderStatus.Cancelled);
            await s.SetUsedCountAsync(created.VoucherId, 3);

            using (var scope = s.Factory.Services.CreateScope())
            {
                var usage = scope.ServiceProvider.GetRequiredService<IVoucherUsageReadRepository>();
                Assert.True(await usage.HasOrderHistoryAsync(created.VoucherId));
                Assert.Equal(3L, await usage.CountNonCancelledOrdersAsync(created.VoucherId));
                Assert.Equal(2L, await usage.CountNonCancelledOrdersAsync(created.VoucherId, s.Customer.Id));
                Assert.True(await usage.HasCustomerUsageAboveLimitAsync(created.VoucherId, 1));
                Assert.False(await usage.HasCustomerUsageAboveLimitAsync(created.VoucherId, 2));
            }

            var lowTotal = ToUpdate(created);
            lowTotal.UsageLimit = 2;
            await ExpectBusinessAsync(BusinessErrorType.Conflict,
                "VOUCHER_USAGE_LIMIT_BELOW_USED_COUNT", () => s.CallAsync(service =>
                    service.UpdateAsync(s.Admin.Id, created.VoucherId, lowTotal)));
            var lowUser = ToUpdate(created);
            lowUser.PerUserLimit = 1;
            await ExpectBusinessAsync(BusinessErrorType.Conflict,
                "VOUCHER_PER_USER_LIMIT_BELOW_USAGE", () => s.CallAsync(service =>
                    service.UpdateAsync(s.Admin.Id, created.VoucherId, lowUser)));

            var unchanged = await s.ReadAsync(created.VoucherId);
            Assert.Equal(created.UsageLimit, unchanged.UsageLimit);
            Assert.Equal(created.PerUserLimit, unchanged.PerUserLimit);
            Assert.Null(unchanged.UpdatedAt);

            var boundary = ToUpdate(created);
            boundary.UsageLimit = 3;
            boundary.PerUserLimit = 2;
            var accepted = await s.CallAsync(service => service.UpdateAsync(s.Admin.Id,
                created.VoucherId, boundary));
            Assert.Equal<int?>(3, accepted.UsageLimit);
            Assert.Equal<int?>(2, accepted.PerUserLimit);
            Assert.Equal(3, accepted.UsedCount);

            // No automatic counter repair in the Admin terms endpoint.
            await s.SetUsedCountAsync(created.VoucherId, 2);
            await ExpectBusinessAsync(BusinessErrorType.Conflict, "VOUCHER_USAGE_INCONSISTENT",
                () => s.CallAsync(service => service.UpdateAsync(s.Admin.Id, created.VoucherId, boundary)));
            Assert.Equal(2, (await s.ReadAsync(created.VoucherId)).UsedCount);
            Assert.False((await s.StatusAsync(created.VoucherId, false)).IsActive);
        });

    [Fact]
    public Task Commands_ShouldRejectUnauthorizedMissingAndInvalidRequests()
        => WithDatabaseAsync(async s =>
        {
            var created = await s.CreateAsync("-A");
            var valid = ToUpdate(created);
            valid.Description = "Should not be saved";
            foreach (var actor in new[] { s.Customer.Id, s.InactiveAdmin.Id, Guid.NewGuid() })
            {
                await ExpectBusinessAsync(BusinessErrorType.Unauthorized, "ADMIN_ACCESS_REQUIRED",
                    () => s.CallAsync(service => service.CreateAsync(actor, NewCreate(s.Tag + "-DENIED"))));
                await ExpectBusinessAsync(BusinessErrorType.Unauthorized, "ADMIN_ACCESS_REQUIRED",
                    () => s.CallAsync(service => service.UpdateAsync(actor, created.VoucherId, valid)));
                await ExpectBusinessAsync(BusinessErrorType.Unauthorized, "ADMIN_ACCESS_REQUIRED",
                    () => s.CallAsync(service => service.UpdateStatusAsync(actor, created.VoucherId,
                        new UpdateVoucherStatusDto { IsActive = false })));
            }

            await ExpectBusinessAsync(BusinessErrorType.NotFound, "VOUCHER_NOT_FOUND",
                () => s.CallAsync(service => service.UpdateAsync(s.Admin.Id, Guid.NewGuid(), valid)));
            await ExpectBusinessAsync(BusinessErrorType.NotFound, "VOUCHER_NOT_FOUND",
                () => s.StatusAsync(Guid.NewGuid(), false));
            await Assert.ThrowsAsync<ValidationException>(() => s.CallAsync(service =>
                service.CreateAsync(Guid.Empty, NewCreate(s.Tag + "-EMPTY"))));
            await Assert.ThrowsAsync<ValidationException>(() => s.CallAsync(service =>
                service.UpdateAsync(s.Admin.Id, Guid.Empty, valid)));
            await Assert.ThrowsAsync<ValidationException>(() => s.CallAsync(service =>
                service.CreateAsync(s.Admin.Id, NewCreate("bad code"))));
            valid.DiscountValue = 101m;
            await Assert.ThrowsAsync<ValidationException>(() => s.CallAsync(service =>
                service.UpdateAsync(s.Admin.Id, created.VoucherId, valid)));
            await Assert.ThrowsAsync<ValidationException>(() => s.CallAsync(service =>
                service.UpdateStatusAsync(s.Admin.Id, created.VoucherId, new UpdateVoucherStatusDto())));
            var stored = await s.ReadAsync(created.VoucherId);
            Assert.True(stored.IsActive);
            Assert.Equal(created.Description, stored.Description);
            Assert.Equal(created.DiscountValue, stored.DiscountValue);
            Assert.Null(stored.UpdatedAt);
            Assert.Equal(1, await s.Db.Vouchers.AsNoTracking().CountAsync(x => x.Code.StartsWith(s.Tag)));
        });

    [Fact]
    public Task ConcurrentCreate_ShouldPersistExactlyOneNormalizedVoucher()
        => WithDatabaseAsync(async s =>
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<string> AttemptAsync(Guid adminId, string code)
            {
                await gate.Task;
                try
                {
                    await s.CallAsync(service => service.CreateAsync(adminId, NewCreate(code)));
                    return "CREATED";
                }
                catch (BusinessException ex) when (ex.ErrorType == BusinessErrorType.Conflict)
                {
                    return ex.Message;
                }
            }

            var first = AttemptAsync(s.Admin.Id, s.Tag + "-A");
            var second = AttemptAsync(s.Admin.Id, "  " + s.Tag.ToLowerInvariant() + "-a  ");
            gate.SetResult(true);
            var results = await Task.WhenAll(first, second);
            Assert.Single(results, x => x == "CREATED");
            Assert.Single(results, x => x == "VOUCHER_CODE_EXISTS");
            var rows = await s.Db.Vouchers.AsNoTracking()
                .Where(x => x.Code == s.Tag + "-A").ToListAsync();
            Assert.Equal(0, Assert.Single(rows).UsedCount);
        });

    [Fact]
    public Task ConcurrentUpdate_ShouldPersistOneCompleteTermsPayload()
        => WithDatabaseAsync(async s =>
        {
            var created = await s.CreateAsync("-A");
            var first = ToUpdate(created);
            first.Description = "Alpha";
            var second = ToUpdate(created);
            second.DiscountValue = 30m;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<VoucherDto> AttemptAsync(UpdateVoucherDto dto)
            {
                await gate.Task;
                return await s.CallAsync(service => service.UpdateAsync(s.Admin.Id, created.VoucherId, dto));
            }
            var a = AttemptAsync(first);
            var b = AttemptAsync(second);
            gate.SetResult(true);
            var results = await Task.WhenAll(a, b);
            Assert.Equal("Alpha", results[0].Description);
            Assert.Equal(created.DiscountValue, results[0].DiscountValue);
            Assert.Equal(created.Description, results[1].Description);
            Assert.Equal(30m, results[1].DiscountValue);
            var final = await s.ReadAsync(created.VoucherId);
            Assert.True((final.Description == "Alpha" && final.DiscountValue == created.DiscountValue) ||
                        (final.Description == created.Description && final.DiscountValue == 30m));
            Assert.Equal(created.Code, final.Code);
            Assert.Equal(0, final.UsedCount);
            Assert.True(final.IsActive);
        });

    private static CreateVoucherDto NewCreate(string code) => new()
    {
        Code = code, Description = "Original promotion", DiscountType = DiscountType.Percentage,
        DiscountValue = 20m, MinOrderAmount = 100000m, MaxDiscountAmount = 50000m,
        UsageLimit = 10, PerUserLimit = 5, StartAt = Start, EndAt = Start.AddDays(3)
    };

    private static UpdateVoucherDto ToUpdate(VoucherDto dto) => new()
    {
        Description = dto.Description, DiscountType = dto.DiscountType,
        DiscountValue = dto.DiscountValue, MinOrderAmount = dto.MinOrderAmount,
        MaxDiscountAmount = dto.MaxDiscountAmount, UsageLimit = dto.UsageLimit,
        PerUserLimit = dto.PerUserLimit, StartAt = new DateTimeOffset(dto.StartAtUtc),
        EndAt = new DateTimeOffset(dto.EndAtUtc)
    };

    private static async Task ExpectBusinessAsync(BusinessErrorType type, string message,
        Func<Task<VoucherDto>> operation)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => { await operation(); });
        Assert.Equal(type, ex.ErrorType);
        Assert.Equal(message, ex.Message);
    }

    private static async Task WithDatabaseAsync(Func<TestState, Task> operation)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        var sql = new SqlConnectionStringBuilder(connection);
        if (!string.Equals(sql.InitialCatalog, "FlowerShop_Test", StringComparison.OrdinalIgnoreCase)
            || sql.MultipleActiveResultSets)
            throw new InvalidOperationException("Use FlowerShop_Test with MARS disabled.");
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), x => x.EndsWith("_InitialCreate"));

        User NewUser(UserRole role, bool isActive = true)
        {
            var user = new User
            {
                FullName = "Voucher Service Test", Email = $"voucher-service-{Guid.NewGuid():N}@example.invalid",
                Role = role, IsActive = isActive
            };
            user.PasswordHash = hasher.HashPassword(user, "FlowerShop#2026");
            return user;
        }

        var state = new TestState(factory, db, $"VS-{Guid.NewGuid():N}".ToUpperInvariant(),
            NewUser(UserRole.Admin), NewUser(UserRole.Customer), NewUser(UserRole.Customer),
            NewUser(UserRole.Admin, false));
        Guid[] userIds = [state.Admin.Id, state.Customer.Id, state.Other.Id, state.InactiveAdmin.Id];
        try
        {
            db.Users.AddRange(state.Admin, state.Customer, state.Other, state.InactiveAdmin);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await operation(state);
        }
        finally
        {
            // No outer transaction: each command opens its own locked transaction.
            // Fixtures only contain Order headers, not order items or payments.
            db.ChangeTracker.Clear();
            await db.Orders.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync();
            await db.Vouchers.Where(x => x.Code.StartsWith(state.Tag)).ExecuteDeleteAsync();
            await db.Users.Where(x => userIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed record TestState(TestApiFactory Factory, FlowerShopDbContext Db, string Tag,
        User Admin, User Customer, User Other, User InactiveAdmin)
    {
        public async Task<VoucherDto> CallAsync(Func<IVoucherService, Task<VoucherDto>> operation)
        {
            using var scope = Factory.Services.CreateScope();
            return await operation(scope.ServiceProvider.GetRequiredService<IVoucherService>());
        }

        public Task<VoucherDto> CreateAsync(string suffix)
            => CallAsync(service => service.CreateAsync(Admin.Id, NewCreate(Tag + suffix)));

        public Task<VoucherDto> StatusAsync(Guid voucherId, bool active)
            => CallAsync(service => service.UpdateStatusAsync(Admin.Id, voucherId,
                new UpdateVoucherStatusDto { IsActive = active }));

        public Task<Voucher> ReadAsync(Guid voucherId)
            => Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == voucherId);

        public async Task SetUsedCountAsync(Guid voucherId, int count)
        {
            var affected = await Db.Vouchers.Where(x => x.Id == voucherId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedCount, count));
            Assert.Equal(1, affected);
        }

        public async Task AddOrderAsync(Guid voucherId, Guid userId, OrderStatus status)
        {
            // Historical-reference fixture, not an end-to-end Checkout test.
            Db.Orders.Add(new Order
            {
                OrderCode = $"VS-{Guid.NewGuid():N}", UserId = userId, VoucherId = voucherId,
                Status = status, RecipientName = "Test Recipient", RecipientPhone = "0901234567",
                ShippingAddressLine = "123 Test Street", ShippingWard = "Ward", ShippingDistrict = "District",
                ShippingProvince = "Da Nang", ShippingCountryCode = "VN",
                Subtotal = 100000m, DiscountAmount = 20000m, TotalAmount = 80000m
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }
    }
}
