using FlowerShop.Application.Common.Concurrency;
using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Services;
using FlowerShop.Domain.Common;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Application;

public sealed class VoucherCancellationConcurrencyTests
{
    [Theory]
    [InlineData(false)] // Two customer requests.
    [InlineData(true)]  // Customer and admin.
    public async Task Cancel_SameOrderConcurrently_ShouldRestoreExactlyOnce(bool secondIsAdmin)
    {
        await WithDatabaseAsync(1, async s =>
        {
            var request = Request(s, s.Alice);
            var order = await CheckoutAsync(s, s.Alice, request);
            var gate = new TwoPartyGate();
            // Synchronize BEFORE the order lock, never inside it.
            async Task<Attempt<OrderStatusUpdateResultDto>> Run(bool admin)
            {
                await gate.ArriveAsync();
                return await CaptureAsync(() => CancelAsync(s, s.Alice, order.OrderId, admin));
            }
            var attempts = await Task.WhenAll(Run(false), Run(secondIsAdmin));
            Assert.Equal(2, gate.Arrivals);
            var winner = Assert.Single(attempts, x => x.Result is not null);
            Assert.Equal(OrderStatus.Cancelled, Assert.IsType<OrderStatusUpdateResultDto>(winner.Result).Status);
            ExpectConflict(Assert.Single(attempts, x => x.Error is not null), "ORDER_ALREADY_CANCELLED");
            await AssertUsageAsync(s, 0, 1);
            await AssertOrderAsync(s, s.Alice, request, order.OrderId, OrderStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
            await AssertShopperAsync(s, s.Bob, 10, 2);
        });
    }

    [Fact]
    public async Task Cancel_AndCheckoutCompetingForLastUse_ShouldPreserveInvariant()
    {
        await WithDatabaseAsync(1, async s =>
        {
            var aliceRequest = Request(s, s.Alice);
            var aliceOrder = await CheckoutAsync(s, s.Alice, aliceRequest);
            var bobRequest = Request(s, s.Bob);
            var gate = new TwoPartyGate();
            // Both reach the shared voucher lock before either can acquire it.
            var cancel = CancelAsync(s, s.Alice, aliceOrder.OrderId, gate: gate);
            var checkout = CaptureAsync(() => CheckoutAsync(s, s.Bob, bobRequest, gate));
            await Task.WhenAll(cancel, checkout);
            Assert.Equal(2, gate.Arrivals);
            Assert.Equal(OrderStatus.Cancelled, (await cancel).Status);
            await AssertOrderAsync(s, s.Alice, aliceRequest, aliceOrder.OrderId, OrderStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);

            var attempt = await checkout;
            CheckoutResultDto bobOrder;
            if (attempt.Result is { } committed)
            {
                // Cancellation won the voucher lock first.
                Assert.Null(attempt.Error);
                bobOrder = committed;
                await AssertUsageAsync(s, 1, 2);
            }
            else
            {
                // Checkout won first and observed the original exhausted voucher.
                ExpectConflict(attempt, "VOUCHER_USAGE_LIMIT_REACHED");
                await AssertUsageAsync(s, 0, 1);
                await AssertShopperAsync(s, s.Bob, 10, 2);
                Assert.False(await s.Db.Orders.AnyAsync(x => x.OrderCode == $"FS-{bobRequest.CheckoutRequestId:N}"));
                bobOrder = await CheckoutAsync(s, s.Bob, bobRequest);
            }
            Assert.False(bobOrder.IsReplay);
            await AssertUsageAsync(s, 1, 2);
            await AssertOrderAsync(s, s.Bob, bobRequest, bobOrder.OrderId, OrderStatus.Pending);
            await AssertShopperAsync(s, s.Bob, 8, 0);
        });
    }

    [Fact]
    public async Task Cancel_TwoOrdersSharingVoucher_ShouldReleaseBothUsesWithoutLostUpdate()
    {
        await WithDatabaseAsync(2, async s =>
        {
            var ar = Request(s, s.Alice);
            var br = Request(s, s.Bob);
            var a = await CheckoutAsync(s, s.Alice, ar);
            var b = await CheckoutAsync(s, s.Bob, br);
            await AssertUsageAsync(s, 2, 2);
            var gate = new TwoPartyGate();
            var results = await Task.WhenAll(
                CancelAsync(s, s.Alice, a.OrderId, gate: gate),
                CancelAsync(s, s.Bob, b.OrderId, gate: gate));
            Assert.Equal(2, gate.Arrivals);
            Assert.All(results, x => Assert.Equal(OrderStatus.Cancelled, x.Status));
            await AssertUsageAsync(s, 0, 2);
            await AssertOrderAsync(s, s.Alice, ar, a.OrderId, OrderStatus.Cancelled);
            await AssertOrderAsync(s, s.Bob, br, b.OrderId, OrderStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
            await AssertShopperAsync(s, s.Bob, 10, 0);
        });
    }

    [Fact]
    public async Task Cancel_AndAdminTermsUpdate_ShouldPreserveCounterAndEditableTerms()
    {
        await WithDatabaseAsync(1, async s =>
        {
            var request = Request(s, s.Alice);
            var order = await CheckoutAsync(s, s.Alice, request);
            var before = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
            var dto = new UpdateVoucherDto
            {
                Description = "Updated while cancelling",
                DiscountType = before.DiscountType, DiscountValue = before.DiscountValue,
                MinOrderAmount = before.MinOrderAmount, MaxDiscountAmount = before.MaxDiscountAmount,
                UsageLimit = 2, PerUserLimit = before.PerUserLimit,
                StartAt = new DateTimeOffset(DateTime.SpecifyKind(before.StartAt, DateTimeKind.Utc)),
                EndAt = new DateTimeOffset(DateTime.SpecifyKind(before.EndAt.AddDays(1), DateTimeKind.Utc))
            };
            var gate = new TwoPartyGate();
            var cancel = CancelAsync(s, s.Alice, order.OrderId, gate: gate);
            var update = UpdateAsync(s, dto, gate);
            await Task.WhenAll(cancel, update);
            Assert.Equal(2, gate.Arrivals);
            Assert.Equal(OrderStatus.Cancelled, (await cancel).Status);
            Assert.Equal(dto.Description, (await update).Description);
            var after = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
            Assert.Equal(dto.Description, after.Description);
            Assert.Equal(2, after.UsageLimit);
            Assert.Equal(before.EndAt.AddDays(1), after.EndAt);
            Assert.Equal(before.StartAt, after.StartAt);
            Assert.Equal(before.Code, after.Code);
            Assert.Equal(before.DiscountValue, after.DiscountValue);
            Assert.Equal(before.IsActive, after.IsActive);
            await AssertUsageAsync(s, 0, 1);
            await AssertOrderAsync(s, s.Alice, request, order.OrderId, OrderStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
        });
    }

    [Fact]
    public async Task Cancel_RollbackWhileCheckoutCompetes_ShouldNotExposePhantomUse()
    {
        await WithDatabaseAsync(1, async s =>
        {
            var ar = Request(s, s.Alice);
            var a = await CheckoutAsync(s, s.Alice, ar);
            // Independent product for Bob: only Alice's restoration can overflow.
            Assert.Equal(1, await s.Db.Products.Where(x => x.Id == s.Alice.Product.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.StockQuantity, int.MaxValue)));
            var before = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
            var br = Request(s, s.Bob);
            var gate = new TwoPartyGate();
            var cancel = CaptureAsync(() => CancelAsync(s, s.Alice, a.OrderId, gate: gate));
            var checkout = CaptureAsync(() => CheckoutAsync(s, s.Bob, br, gate));
            await Task.WhenAll(cancel, checkout);
            Assert.Equal(2, gate.Arrivals);
            ExpectConflict(await cancel, "STOCK_RESTORATION_FAILED");
            ExpectConflict(await checkout, "VOUCHER_USAGE_LIMIT_REACHED");
            await AssertUsageAsync(s, 1, 1);
            Assert.Equal(before.UpdatedAt, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .Select(x => x.UpdatedAt).SingleAsync());
            await AssertOrderAsync(s, s.Alice, ar, a.OrderId, OrderStatus.Pending);
            await AssertShopperAsync(s, s.Alice, int.MaxValue, 0);
            await AssertShopperAsync(s, s.Bob, 10, 2);
            Assert.False(await s.Db.Orders.AnyAsync(x => x.OrderCode == $"FS-{br.CheckoutRequestId:N}"));

            Assert.Equal(1, await s.Db.Products.Where(x => x.Id == s.Alice.Product.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.StockQuantity, 8)));
            await CancelAsync(s, s.Alice, a.OrderId);
            var retry = await CheckoutAsync(s, s.Bob, br);
            Assert.False(retry.IsReplay);
            await AssertUsageAsync(s, 1, 2);
            await AssertOrderAsync(s, s.Alice, ar, a.OrderId, OrderStatus.Cancelled);
            await AssertOrderAsync(s, s.Bob, br, retry.OrderId, OrderStatus.Pending);
            await AssertShopperAsync(s, s.Alice, 10, 0);
            await AssertShopperAsync(s, s.Bob, 8, 0);
        });
    }

    [Fact]
    public async Task Checkout_AfterCancellation_ShouldAllowNewUseButNotConsumeOnReplay()
    {
        await WithDatabaseAsync(10, async s =>
        {
            var first = Request(s, s.Alice);
            var order = await CheckoutAsync(s, s.Alice, first);
            await CancelAsync(s, s.Alice, order.OrderId);
            await AssertUsageAsync(s, 0, 1);
            // Replay of a cancelled order returns the historical result, not a new order.
            var replay = await CheckoutAsync(s, s.Alice, first);
            Assert.True(replay.IsReplay);
            Assert.Equal(order.OrderId, replay.OrderId);
            Assert.Equal(OrderStatus.Cancelled, replay.Status);
            Assert.Equal(PaymentStatus.Cancelled, replay.PaymentStatus);
            Assert.Equal(order.TotalAmount, replay.TotalAmount);
            await AssertUsageAsync(s, 0, 1);
            await AssertShopperAsync(s, s.Alice, 10, 0);

            s.Db.CartItems.Add(new CartItem { CartId = s.Alice.Cart.Id, ProductId = s.Alice.Product.Id, Quantity = 2 });
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
            var second = Request(s, s.Alice);
            var next = await CheckoutAsync(s, s.Alice, second);
            Assert.False(next.IsReplay);
            Assert.NotEqual(order.OrderId, next.OrderId);
            await AssertUsageAsync(s, 1, 2);
            await AssertOrderAsync(s, s.Alice, first, order.OrderId, OrderStatus.Cancelled);
            await AssertOrderAsync(s, s.Alice, second, next.OrderId, OrderStatus.Pending);
            await AssertShopperAsync(s, s.Alice, 8, 0);
        });
    }

    [Fact]
    public async Task Replay_CancellationCommitsAfterInitialLookup_ShouldReturnFreshConsistentStatuses()
    {
        await WithDatabaseAsync(1, async s =>
        {
            var request = Request(s, s.Alice);
            var original = await CheckoutAsync(s, s.Alice, request);
            await AssertUsageAsync(s, 1, 1);
            using (var scope = s.Factory.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var inner = sp.GetRequiredService<IUnitOfWork>();
                // Deterministically commit cancellation in a separate scope between
                // the replay's first order lookup and acquisition of its order lock.
                // The callback runs before the real lock; no circular wait is created.
                var uow = new BeforeReplayOrderLock(inner, $"flower-shop:order:{original.OrderId:N}",
                    async ct =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var cancelled = await CancelAsync(s, s.Alice, original.OrderId);
                        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
                    });
                var service = new CheckoutService(uow, sp.GetRequiredService<IProductStockRepository>(),
                    sp.GetRequiredService<ICheckoutPricingService>(),
                    sp.GetRequiredService<IVoucherUsageWriteRepository>(), new CheckoutVoucherRollout(true));
                var replay = await service.CheckoutAsync(s.Alice.User.Id, request);
                Assert.Equal(1, uow.Calls);
                Assert.True(replay.IsReplay);
                Assert.Equal(original.OrderId, replay.OrderId);
                Assert.Equal(OrderStatus.Cancelled, replay.Status);
                Assert.Equal(PaymentStatus.Cancelled, replay.PaymentStatus);
                Assert.Equal(original.DiscountAmount, replay.DiscountAmount);
                Assert.Equal(original.TotalAmount, replay.TotalAmount);
            }
            await AssertUsageAsync(s, 0, 1);
            await AssertOrderAsync(s, s.Alice, request, original.OrderId, OrderStatus.Cancelled);
            await AssertShopperAsync(s, s.Alice, 10, 0);
        });
    }

    // Test-only deterministic interleaving. No database write or lock is mocked.
    private sealed class BeforeReplayOrderLock(IUnitOfWork inner, string orderKey,
        Func<CancellationToken, Task> beforeLock) : IUnitOfWork
    {
        public int Calls { get; private set; }
        public IGenericRepository<T> GetRepository<T>() where T : BaseEntity => inner.GetRepository<T>();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => inner.SaveChangesAsync(cancellationToken);
        public Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default) => inner.ExecuteInTransactionAsync(operation, cancellationToken);
        public Task<TResult> ExecuteInLockedTransactionAsync<TResult>(string resource,
            Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => inner.ExecuteInLockedTransactionAsync(resource, operation, cancellationToken);
        public async Task AcquireTransactionLockAsync(string resource, CancellationToken cancellationToken = default)
        {
            if (StringComparer.Ordinal.Equals(resource, orderKey))
            {
                Calls++;
                await beforeLock(cancellationToken);
            }
            await inner.AcquireTransactionLockAsync(resource, cancellationToken);
        }
    }

    private static CheckoutRequestDto Request(State s, Shopper shopper) => new()
    {
        CheckoutRequestId = Guid.NewGuid(), ShippingAddressId = shopper.Address.Id,
        PaymentMethod = PaymentMethod.CashOnDelivery, VoucherCode = s.Voucher.Code
    };

    private static IUnitOfWork Uow(State s, IServiceProvider sp, TwoPartyGate? gate)
    {
        var inner = sp.GetRequiredService<IUnitOfWork>();
        return gate is null ? inner : new GatedUnitOfWork(inner, VoucherLockKeys.ForId(s.Voucher.Id), gate);
    }

    private static async Task<CheckoutResultDto> CheckoutAsync(State s, Shopper shopper, CheckoutRequestDto request,
        TwoPartyGate? gate = null)
    {
        using var scope = s.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.False(sp.GetRequiredService<CheckoutVoucherRollout>().Enabled);
        var uow = Uow(s, sp, gate);
        var pricing = new CheckoutPricingService(uow, sp.GetRequiredService<IVoucherUsageReadRepository>(),
            sp.GetRequiredService<IUserAccessService>(), sp.GetRequiredService<TimeProvider>());
        var service = new CheckoutService(uow, sp.GetRequiredService<IProductStockRepository>(), pricing,
            sp.GetRequiredService<IVoucherUsageWriteRepository>(), new CheckoutVoucherRollout(true));
        return await service.CheckoutAsync(shopper.User.Id, request);
    }

    private static async Task<OrderStatusUpdateResultDto> CancelAsync(State s, Shopper shopper, Guid orderId,
        bool admin = false, TwoPartyGate? gate = null)
    {
        using var scope = s.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = gate is null ? sp.GetRequiredService<IOrderCancellationService>() :
            new OrderCancellationService(Uow(s, sp, gate), sp.GetRequiredService<IProductStockRepository>(),
                sp.GetRequiredService<IVoucherUsageReadRepository>(), sp.GetRequiredService<IVoucherUsageWriteRepository>());
        return admin ? await service.CancelByAdminAsync(s.Admin.Id, orderId) :
            await service.CancelByCustomerAsync(shopper.User.Id, orderId);
    }

    private static async Task<VoucherDto> UpdateAsync(State s, UpdateVoucherDto dto, TwoPartyGate gate)
    {
        using var scope = s.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = new VoucherService(sp.GetRequiredService<IGenericRepository<Voucher>>(),
            sp.GetRequiredService<IVoucherUsageReadRepository>(), sp.GetRequiredService<IUserAccessService>(),
            Uow(s, sp, gate), sp.GetRequiredService<TimeProvider>());
        return await service.UpdateAsync(s.Admin.Id, s.Voucher.Id, dto);
    }

    private static async Task<Attempt<T>> CaptureAsync<T>(Func<Task<T>> action) where T : class
    {
        try { return new(await action(), null); }
        catch (BusinessException ex) { return new(null, ex); }
    }

    private static void ExpectConflict<T>(Attempt<T> attempt, string code) where T : class
    {
        Assert.Null(attempt.Result);
        var error = Assert.IsType<BusinessException>(attempt.Error);
        Assert.Equal(BusinessErrorType.Conflict, error.ErrorType);
        Assert.Equal(code, error.Message);
    }

    private static async Task AssertUsageAsync(State s, int expected, int history)
    {
        var voucher = await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == s.Voucher.Id);
        var actual = await s.Db.Orders.LongCountAsync(x => x.VoucherId == voucher.Id && x.Status != OrderStatus.Cancelled);
        Assert.Equal(expected, voucher.UsedCount);
        Assert.Equal((long)voucher.UsedCount, actual);
        Assert.True(voucher.UsedCount >= 0);
        Assert.True(!voucher.UsageLimit.HasValue || voucher.UsedCount <= voucher.UsageLimit.Value);
        Assert.Equal(history, await s.Db.Orders.CountAsync(x => x.VoucherId == voucher.Id));
        Assert.Equal(history, await s.Db.Orders.CountAsync(x => x.UserId == s.Alice.User.Id || x.UserId == s.Bob.User.Id));
    }

    private static async Task AssertOrderAsync(State s, Shopper shopper, CheckoutRequestDto request, Guid orderId, OrderStatus status)
    {
        var order = await s.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId);
        Assert.Equal(shopper.User.Id, order.UserId);
        Assert.Equal(s.Voucher.Id, order.VoucherId);
        Assert.Equal($"FS-{request.CheckoutRequestId:N}", order.OrderCode);
        Assert.Equal(CheckoutRequestFingerprint.Compute(request), order.CheckoutRequestFingerprint);
        Assert.Equal(status, order.Status);
        Assert.Equal(500000m, order.Subtotal);
        Assert.Equal(0m, order.ShippingFee);
        Assert.Equal(100000m, order.DiscountAmount);
        Assert.Equal(400000m, order.TotalAmount);
        var payment = await s.Db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal(status == OrderStatus.Cancelled ? PaymentStatus.Cancelled : PaymentStatus.Pending, payment.Status);
        Assert.Equal(PaymentMethod.CashOnDelivery, payment.Method);
        Assert.Equal("VND", payment.Currency);
        Assert.Null(payment.PaidAt);
        Assert.Equal(order.TotalAmount, payment.Amount);
        var item = await s.Db.OrderItems.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal(shopper.Product.Id, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(250000m, item.UnitPrice);
        Assert.Equal(500000m, item.LineTotal);
    }

    private static async Task AssertShopperAsync(State s, Shopper shopper, int stock, int cartQuantity)
    {
        Assert.Equal(stock, await s.Db.Products.Where(x => x.Id == shopper.Product.Id).Select(x => x.StockQuantity).SingleAsync());
        Assert.Equal(cartQuantity, await s.Db.CartItems.Where(x => x.CartId == shopper.Cart.Id).SumAsync(x => x.Quantity));
    }

    // A barrier before lock acquisition, not a timing-dependent sleep. The timeout
    // makes a missing participant fail the test rather than hang indefinitely.
    private sealed class TwoPartyGate
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public int Arrivals => Volatile.Read(ref arrivals);
        public async Task ArriveAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
                ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    // Delegate every operation to the real scoped UoW. Synchronize only at
    // the voucher lock boundary, before any voucher/stock mutation.
    private sealed class GatedUnitOfWork(IUnitOfWork inner, string voucherKey, TwoPartyGate gate) : IUnitOfWork
    {
        public IGenericRepository<T> GetRepository<T>() where T : BaseEntity => inner.GetRepository<T>();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => inner.SaveChangesAsync(cancellationToken);
        public Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default) => inner.ExecuteInTransactionAsync(operation, cancellationToken);
        public async Task<TResult> ExecuteInLockedTransactionAsync<TResult>(string resource,
            Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            if (StringComparer.Ordinal.Equals(resource, voucherKey))
                await gate.ArriveAsync(cancellationToken);
            return await inner.ExecuteInLockedTransactionAsync(resource, operation, cancellationToken);
        }
        public async Task AcquireTransactionLockAsync(string resource, CancellationToken cancellationToken = default)
        {
            if (StringComparer.Ordinal.Equals(resource, voucherKey))
                await gate.ArriveAsync(cancellationToken);
            await inner.AcquireTransactionLockAsync(resource, cancellationToken);
        }
    }

    private static async Task WithDatabaseAsync(int limit, Func<State, Task> action)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        Assert.Contains("20261124130728_AddVoucherUsageCompositeIndex", await db.Database.GetAppliedMigrationsAsync());
        User NewUser(UserRole role) => new()
        {
            FullName = "Voucher Concurrency Test", Email = $"vcc-{Guid.NewGuid():N}@example.invalid",
            PasswordHash = "not-used-for-login", Role = role, IsActive = true
        };
        var category = new Category { Name = "Voucher Concurrency Test", Slug = $"vcc-cat-{Guid.NewGuid():N}" };
        Shopper NewShopper()
        {
            var user = NewUser(UserRole.Customer);
            var product = new Product { CategoryId = category.Id, Name = "Rose", Slug = $"vcc-product-{Guid.NewGuid():N}",
                Price = 250000m, StockQuantity = 10, IsActive = true };
            var cart = new Cart { UserId = user.Id };
            var address = new UserAddress { UserId = user.Id, RecipientName = "Test Customer", RecipientPhone = "0900000000",
                AddressLine = "Test Street", Ward = "Ward", District = "District", Province = "Da Nang", CountryCode = "VN", IsDefault = true };
            return new Shopper(user, product, cart, address);
        }
        var alice = NewShopper();
        var bob = NewShopper();
        var admin = NewUser(UserRole.Admin);
        var now = DateTime.UtcNow;
        var voucher = new Voucher { Code = $"VCC-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
            DiscountValue = 20m, MinOrderAmount = 300000m, UsageLimit = limit, PerUserLimit = 1,
            UsedCount = 0, IsActive = true, StartAt = now.AddDays(-1), EndAt = now.AddDays(1) };
        Guid[] userIds = [alice.User.Id, bob.User.Id, admin.Id];
        Guid[] cartIds = [alice.Cart.Id, bob.Cart.Id];
        Guid[] productIds = [alice.Product.Id, bob.Product.Id];
        try
        {
            db.AddRange(category, admin, voucher);
            foreach (var shopper in new[] { alice, bob })
            {
                db.AddRange(shopper.User, shopper.Product, shopper.Cart, shopper.Address);
                db.CartItems.Add(new CartItem { CartId = shopper.Cart.Id, ProductId = shopper.Product.Id, Quantity = 2 });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await action(new State(factory, db, voucher, alice, bob, admin));
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orderIds = await db.Orders.Where(x => userIds.Contains(x.UserId)).Select(x => x.Id).ToArrayAsync();
            await db.Payments.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.OrderItems.Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(x => orderIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.CartItems.Where(x => cartIds.Contains(x.CartId)).ExecuteDeleteAsync();
            await db.Carts.Where(x => cartIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.UserAddresses.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync();
            await db.Products.Where(x => productIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.Categories.Where(x => x.Id == category.Id).ExecuteDeleteAsync();
            await db.Vouchers.Where(x => x.Id == voucher.Id).ExecuteDeleteAsync();
            await db.Users.Where(x => userIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed record Attempt<T>(T? Result, BusinessException? Error) where T : class;
    private sealed record Shopper(User User, Product Product, Cart Cart, UserAddress Address);
    private sealed record State(TestApiFactory Factory, FlowerShopDbContext Db, Voucher Voucher, Shopper Alice, Shopper Bob, User Admin);
}
