using FlowerShop.Application.Common.Exceptions;
using FlowerShop.Application.Common.Idempotency;
using FlowerShop.Application.Common.Pricing;
using FlowerShop.Application.DTOs.Orders;
using FlowerShop.Application.DTOs.Vouchers;
using FlowerShop.Application.Interfaces.Services;
using FlowerShop.Application.Services;
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

public sealed class CheckoutVoucherIntegrationTests
{
    private static readonly DateTime Now = new(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(DiscountType.Percentage, 120000)]
    [InlineData(DiscountType.FixedAmount, 75000)]
    public async Task Checkout_ShouldMatchPreviewAndPersistFinancialSnapshots(
        DiscountType type, int expectedDiscount)
    {
        await WithDatabaseAsync(async s =>
        {
            if (type == DiscountType.FixedAmount)
            {
                Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                    .ExecuteUpdateAsync(p => p.SetProperty(x => x.DiscountType, type)
                        .SetProperty(x => x.DiscountValue, 75000m)
                        .SetProperty(x => x.MaxDiscountAmount, (decimal?)null)));
            }
            var quote = await PreviewAsync(s, s.Alice, Now);
            Assert.Equal(500000m, quote.Subtotal);
            Assert.Equal((decimal)expectedDiscount, quote.DiscountAmount);
            await AssertInitialAsync(s);

            var request = Request(s, s.Alice);
            request.VoucherCode = $" {s.Voucher.Code.ToLowerInvariant()} ";
            var result = await RunAsync(s, s.Alice, request);
            Assert.False(result.IsReplay);
            Assert.Equal(quote.Subtotal, result.Subtotal);
            Assert.Equal(quote.ShippingFee, result.ShippingFee);
            Assert.Equal(quote.DiscountAmount, result.DiscountAmount);
            Assert.Equal(quote.TotalAmount, result.TotalAmount);
            await AssertCommittedAsync(s, s.Alice, request, result, expectedDiscount);
            Assert.Equal(1, await UsedAsync(s));
            Assert.Equal(1, await LinkedCountAsync(s));
            Assert.Equal(1, await OrderCountAsync(s));
            Assert.Equal(8, await StockAsync(s, s.Alice));
            Assert.Equal(10, await StockAsync(s, s.Bob));
            Assert.Equal(0, await CartQuantityAsync(s, s.Alice));
            Assert.Equal(2, await CartQuantityAsync(s, s.Bob));
        });
    }

    [Fact]
    public async Task Checkout_ShouldRejectVoucherAtEndBoundaryAfterSuccessfulPreview()
    {
        await WithDatabaseAsync(async s =>
        {
            var quote = await PreviewAsync(s, s.Alice, s.Voucher.EndAt.AddMilliseconds(-1));
            Assert.Equal(120000m, quote.DiscountAmount);
            Assert.Equal(s.Voucher.EndAt.AddMilliseconds(-1), quote.QuotedAtUtc);
            var request = Request(s, s.Alice);
            await ExpectConflictAsync("VOUCHER_EXPIRED", () => RunAsync(s, s.Alice, request, s.Voucher.EndAt));
            await AssertInitialAsync(s);
        });
    }

    [Fact]
    public async Task Checkout_ShouldEnforcePerUserLimitOnNewRequestWithoutConsumingAgain()
    {
        await WithDatabaseAsync(async s =>
        {
            var first = Request(s, s.Alice);
            var created = await RunAsync(s, s.Alice, first);
            await AssertCommittedAsync(s, s.Alice, first, created, 120000m);

            // Refill the cart for a genuinely new checkout, not an idempotent retry.
            s.Db.CartItems.Add(new CartItem { CartId = s.Alice.Cart.Id, ProductId = s.Alice.Product.Id, Quantity = 2 });
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
            var second = Request(s, s.Alice);
            await ExpectConflictAsync("VOUCHER_PER_USER_LIMIT_REACHED", () => RunAsync(s, s.Alice, second));
            Assert.Equal(1, await UsedAsync(s));
            Assert.Equal(1, await LinkedCountAsync(s));
            Assert.Equal(1, await OrderCountAsync(s));
            Assert.Equal(8, await StockAsync(s, s.Alice));
            Assert.Equal(2, await CartQuantityAsync(s, s.Alice));
            Assert.False(await s.Db.Orders.AnyAsync(x => x.OrderCode == $"FS-{second.CheckoutRequestId:N}"));
        });
    }

    [Fact]
    public async Task Checkout_TwoCustomersCompetingForLastUse_ShouldCommitExactlyOneOrder()
    {
        await WithDatabaseAsync(async s =>
        {
            Assert.Equal(1, await s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.UsageLimit, 1)));
            var a = Request(s, s.Alice);
            var b = Request(s, s.Bob);
            var gate = new TwoPartyGate();
            // Different carts AND different products: only the voucher is contested.
            // Both requests reach pricing while holding their own cart locks.
            var attempts = await Task.WhenAll(
                CaptureAsync(() => RunAsync(s, s.Alice, a, pricingGate: gate)),
                CaptureAsync(() => RunAsync(s, s.Bob, b, pricingGate: gate)));
            Assert.Equal(2, gate.Arrivals);
            var won = Assert.Single(attempts, x => x.Result is not null);
            var lost = Assert.Single(attempts, x => x.Error is not null);
            var error = Assert.IsType<BusinessException>(lost.Error);
            Assert.Equal(BusinessErrorType.Conflict, error.ErrorType);
            Assert.Equal("VOUCHER_USAGE_LIMIT_REACHED", error.Message);
            var result = Assert.IsType<CheckoutResultDto>(won.Result);
            Assert.False(result.IsReplay);
            var winner = result.OrderCode == $"FS-{a.CheckoutRequestId:N}" ? s.Alice : s.Bob;
            var loser = winner == s.Alice ? s.Bob : s.Alice;
            var winnerRequest = winner == s.Alice ? a : b;
            await AssertCommittedAsync(s, winner, winnerRequest, result, 120000m);
            Assert.Equal(1, await UsedAsync(s));
            Assert.Equal(1, await LinkedCountAsync(s));
            Assert.Equal(1, await OrderCountAsync(s));
            Assert.Equal(8, await StockAsync(s, winner));
            Assert.Equal(10, await StockAsync(s, loser));
            Assert.Equal(0, await CartQuantityAsync(s, winner));
            Assert.Equal(2, await CartQuantityAsync(s, loser));
        });
    }

    [Fact]
    public async Task Checkout_ConcurrentEquivalentRequests_ShouldConsumeExactlyOnce()
    {
        await WithDatabaseAsync(async s =>
        {
            var a = Request(s, s.Alice);
            var b = Request(s, s.Alice);
            b.CheckoutRequestId = a.CheckoutRequestId;
            b.VoucherCode = $" {s.Voucher.Code.ToLowerInvariant()} ";
            var start = new TwoPartyGate();
            async Task<CheckoutResultDto> RunTogether(CheckoutRequestDto request)
            {
                // Gate outside the transaction: a gate inside the same cart lock
                // would make the first request wait for a request that cannot enter.
                await start.ArriveAsync(CancellationToken.None);
                return await RunAsync(s, s.Alice, request);
            }
            var results = await Task.WhenAll(RunTogether(a), RunTogether(b));
            var created = Assert.Single(results, x => !x.IsReplay);
            var replay = Assert.Single(results, x => x.IsReplay);
            Assert.Equal(created.OrderId, replay.OrderId);
            Assert.Equal(created.TotalAmount, replay.TotalAmount);
            Assert.Equal(created.DiscountAmount, replay.DiscountAmount);
            await AssertCommittedAsync(s, s.Alice, a, created, 120000m);
            Assert.Equal(1, await UsedAsync(s));
            Assert.Equal(1, await LinkedCountAsync(s));
            Assert.Equal(1, await OrderCountAsync(s));
            Assert.Equal(8, await StockAsync(s, s.Alice));
            Assert.Equal(0, await CartQuantityAsync(s, s.Alice));
        });
    }

    [Fact]
    public async Task Checkout_SecondStockUpdateFailure_ShouldRollbackUsageAndFirstStockUpdate()
    {
        await WithDatabaseAsync(async s =>
        {
            // Two distinct products in Alice's cart. Whichever sorts first succeeds.
            s.Db.CartItems.Add(new CartItem { CartId = s.Alice.Cart.Id, ProductId = s.Bob.Product.Id, Quantity = 1 });
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
            var request = Request(s, s.Alice);
            using (var scope = s.Factory.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<FlowerShopDbContext>();
                var stock = new FailSecondStockUpdate(sp.GetRequiredService<IProductStockRepository>(), db, s.Voucher.Id);
                var service = NewEnabledService(sp, Now, stock: stock);
                await ExpectConflictAsync("INSUFFICIENT_STOCK", () => service.CheckoutAsync(s.Alice.User.Id, request));
                Assert.Equal(2, stock.Attempts);
                Assert.Null(db.Database.CurrentTransaction);
            }
            // Fresh setup context, no tracked entities; verify every persisted write rolled back.
            Assert.Equal(0, await UsedAsync(s));
            Assert.Equal(0, await LinkedCountAsync(s));
            Assert.Equal(0, await OrderCountAsync(s));
            Assert.Equal(10, await StockAsync(s, s.Alice));
            Assert.Equal(10, await StockAsync(s, s.Bob));
            Assert.Equal(3, await CartQuantityAsync(s, s.Alice));
            Assert.Equal(2, await s.Db.CartItems.CountAsync(x => x.CartId == s.Alice.Cart.Id));
            Assert.Equal(2, await CartQuantityAsync(s, s.Bob));
            Assert.False(await s.Db.Payments.AnyAsync(x => x.Order.UserId == s.Alice.User.Id));
            Assert.False(await s.Db.OrderItems.AnyAsync(x => x.Order.UserId == s.Alice.User.Id));
            // No commit: retrying the exact request with the real stock repository is valid.
            var retry = await RunAsync(s, s.Alice, request);
            Assert.False(retry.IsReplay);
            Assert.Equal(750000m, retry.Subtotal);
            Assert.Equal(120000m, retry.DiscountAmount);
            Assert.Equal(630000m, retry.TotalAmount);
            Assert.Equal(1, await UsedAsync(s));
            Assert.Equal(1, await LinkedCountAsync(s));
            Assert.Equal(1, await OrderCountAsync(s));
            Assert.Equal(8, await StockAsync(s, s.Alice));
            Assert.Equal(9, await StockAsync(s, s.Bob));
            Assert.Equal(0, await CartQuantityAsync(s, s.Alice));
        });
    }

    [Fact]
    public async Task Checkout_WithoutVoucher_ShouldPreserveV1AndNeverConsumeVoucher()
    {
        await WithDatabaseAsync(async s =>
        {
            var request = Request(s, s.Alice);
            request.VoucherCode = null;
            using var scope = s.Factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ICheckoutService>();
            Assert.False(scope.ServiceProvider.GetRequiredService<CheckoutVoucherRollout>().Enabled);
            var result = await service.CheckoutAsync(s.Alice.User.Id, request);
            Assert.False(result.IsReplay);
            await AssertCommittedAsync(s, s.Alice, request, result, 0m);
            // Fresh scope for retry as in a real request.
            request.VoucherCode = " \t ";
            using var replayScope = s.Factory.Services.CreateScope();
            var replay = await replayScope.ServiceProvider.GetRequiredService<ICheckoutService>()
                .CheckoutAsync(s.Alice.User.Id, request);
            Assert.True(replay.IsReplay);
            Assert.Equal(result.OrderId, replay.OrderId);
            Assert.Equal(0, await UsedAsync(s));
            Assert.Equal(0, await LinkedCountAsync(s));
            Assert.Equal(1, await OrderCountAsync(s));
            Assert.Equal(8, await StockAsync(s, s.Alice));
            Assert.Equal(0, await CartQuantityAsync(s, s.Alice));
        });
    }

    private static CheckoutRequestDto Request(State s, Shopper shopper) => new()
    {
        CheckoutRequestId = Guid.NewGuid(), ShippingAddressId = shopper.Address.Id,
        PaymentMethod = PaymentMethod.CashOnDelivery, VoucherCode = s.Voucher.Code
    };

    private static CheckoutService NewEnabledService(IServiceProvider sp, DateTime now,
        TwoPartyGate? pricingGate = null, IProductStockRepository? stock = null)
    {
        Assert.False(sp.GetRequiredService<CheckoutVoucherRollout>().Enabled);
        var uow = sp.GetRequiredService<IUnitOfWork>();
        ICheckoutPricingService pricing = new CheckoutPricingService(uow,
            sp.GetRequiredService<IVoucherUsageReadRepository>(),
            sp.GetRequiredService<IUserAccessService>(), new FixedClock(now));
        if (pricingGate is not null)
            pricing = new GatedPricing(pricing, pricingGate);
        return new CheckoutService(uow, stock ?? sp.GetRequiredService<IProductStockRepository>(),
            pricing, sp.GetRequiredService<IVoucherUsageWriteRepository>(), new CheckoutVoucherRollout(true));
    }

    private static async Task<CheckoutResultDto> RunAsync(State s, Shopper shopper, CheckoutRequestDto request,
        DateTime? now = null, TwoPartyGate? pricingGate = null)
    {
        using var scope = s.Factory.Services.CreateScope();
        return await NewEnabledService(scope.ServiceProvider, now ?? Now, pricingGate)
            .CheckoutAsync(shopper.User.Id, request);
    }

    private static async Task<VoucherPreviewResultDto> PreviewAsync(State s, Shopper shopper, DateTime now)
    {
        using var scope = s.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = new VoucherPreviewService(sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IVoucherUsageReadRepository>(), sp.GetRequiredService<IUserAccessService>(),
            new FixedClock(now));
        return await service.PreviewAsync(shopper.User.Id, new VoucherPreviewRequestDto { VoucherCode = s.Voucher.Code });
    }

    private static async Task ExpectConflictAsync(string code, Func<Task<CheckoutResultDto>> action)
    {
        var error = await Assert.ThrowsAsync<BusinessException>(() => action());
        Assert.Equal(BusinessErrorType.Conflict, error.ErrorType);
        Assert.Equal(code, error.Message);
    }

    private static async Task<Attempt> CaptureAsync(Func<Task<CheckoutResultDto>> action)
    {
        try { return new Attempt(await action(), null); }
        catch (BusinessException error) { return new Attempt(null, error); }
    }

    private static Task<int> UsedAsync(State s) => s.Db.Vouchers.Where(x => x.Id == s.Voucher.Id)
        .Select(x => x.UsedCount).SingleAsync();
    private static Task<int> LinkedCountAsync(State s) => s.Db.Orders.CountAsync(x =>
        x.VoucherId == s.Voucher.Id && x.Status != OrderStatus.Cancelled);
    private static Task<int> OrderCountAsync(State s) => s.Db.Orders.CountAsync(x =>
        x.UserId == s.Alice.User.Id || x.UserId == s.Bob.User.Id);
    private static Task<int> StockAsync(State s, Shopper shopper) => s.Db.Products
        .Where(x => x.Id == shopper.Product.Id).Select(x => x.StockQuantity).SingleAsync();
    private static Task<int> CartQuantityAsync(State s, Shopper shopper) => s.Db.CartItems
        .Where(x => x.CartId == shopper.Cart.Id).SumAsync(x => x.Quantity);

    private static async Task AssertInitialAsync(State s)
    {
        Assert.Equal(0, await UsedAsync(s));
        Assert.Equal(0, await LinkedCountAsync(s));
        Assert.Equal(0, await OrderCountAsync(s));
        foreach (var shopper in new[] { s.Alice, s.Bob })
        {
            Assert.Equal(10, await StockAsync(s, shopper));
            Assert.Equal(2, await CartQuantityAsync(s, shopper));
        }
    }

    private static async Task AssertCommittedAsync(State s, Shopper shopper, CheckoutRequestDto request,
        CheckoutResultDto result, decimal expectedDiscount)
    {
        var order = await s.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == result.OrderId);
        Assert.Equal(shopper.User.Id, order.UserId);
        Assert.Equal($"FS-{request.CheckoutRequestId:N}", order.OrderCode);
        Guid? expectedVoucher = expectedDiscount == 0m ? null : s.Voucher.Id;
        Assert.Equal(expectedVoucher, order.VoucherId);
        Assert.Equal(CheckoutRequestFingerprint.Compute(request), order.CheckoutRequestFingerprint);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(500000m, order.Subtotal);
        Assert.Equal(0m, order.ShippingFee);
        Assert.Equal(expectedDiscount, order.DiscountAmount);
        Assert.Equal(500000m - expectedDiscount, order.TotalAmount);
        Assert.Equal(order.TotalAmount, result.TotalAmount);
        Assert.Equal(order.DiscountAmount, result.DiscountAmount);
        var payment = await s.Db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal(PaymentMethod.CashOnDelivery, payment.Method);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal("VND", payment.Currency);
        Assert.Equal(order.TotalAmount, payment.Amount);
        var item = await s.Db.OrderItems.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal(shopper.Product.Id, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(250000m, item.UnitPrice);
        Assert.Equal(500000m, item.LineTotal);
    }

    // Test-only synchronization. No Thread.Sleep or timing assumptions about lock ownership.
    private sealed class TwoPartyGate
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public int Arrivals => Volatile.Read(ref arrivals);
        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
                ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private sealed class GatedPricing(ICheckoutPricingService inner, TwoPartyGate gate) : ICheckoutPricingService
    {
        public async Task<CheckoutPrice> PrepareAsync(Guid userId, string? voucherCode, decimal subtotal,
            CancellationToken cancellationToken = default)
        {
            await gate.ArriveAsync(cancellationToken);
            return await inner.PrepareAsync(userId, voucherCode, subtotal, cancellationToken);
        }
    }

    private sealed class FailSecondStockUpdate(IProductStockRepository inner, FlowerShopDbContext db, Guid voucherId)
        : IProductStockRepository
    {
        public int Attempts { get; private set; }
        private Guid firstProductId;
        private int firstQuantity;

        public Task<bool> TryIncreaseAsync(Guid productId, int quantity, CancellationToken cancellationToken = default)
            => inner.TryIncreaseAsync(productId, quantity, cancellationToken);

        public async Task<bool> TryDecreaseAsync(Guid productId, int quantity, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1)
            {
                firstProductId = productId;
                firstQuantity = quantity;
                return await inner.TryDecreaseAsync(productId, quantity, cancellationToken);
            }
            Assert.Equal(2, Attempts);
            Assert.NotNull(db.Database.CurrentTransaction);
            Assert.Equal(1, await db.Vouchers.Where(x => x.Id == voucherId).Select(x => x.UsedCount).SingleAsync(cancellationToken));
            Assert.Equal(10 - firstQuantity, await db.Products.Where(x => x.Id == firstProductId)
                .Select(x => x.StockQuantity).SingleAsync(cancellationToken));
            // Deterministic fault injection INSIDE this transaction, not a real external race.
            // The actual conditional stock repository must now reject the second update.
            Assert.Equal(1, await db.Products.Where(x => x.Id == productId)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.StockQuantity, 0), cancellationToken));
            var decreased = await inner.TryDecreaseAsync(productId, quantity, cancellationToken);
            Assert.False(decreased);
            return decreased;
        }
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

    private static async Task WithDatabaseAsync(Func<State, Task> action)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
        using var factory = new TestApiFactory(connection);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var category = new Category { Name = "Checkout Voucher Test", Slug = $"cve-cat-{Guid.NewGuid():N}" };
        Shopper NewShopper()
        {
            var user = new User { FullName = "Checkout Voucher Test", Email = $"cve-{Guid.NewGuid():N}@example.invalid",
                PasswordHash = "not-used-for-login", Role = UserRole.Customer, IsActive = true };
            var product = new Product { CategoryId = category.Id, Name = "Rose", Slug = $"cve-product-{Guid.NewGuid():N}",
                Price = 250000m, StockQuantity = 10, IsActive = true };
            var cart = new Cart { UserId = user.Id };
            var address = new UserAddress { UserId = user.Id, RecipientName = "Test Customer", RecipientPhone = "0900000000",
                AddressLine = "Test Street", Ward = "Ward", District = "District", Province = "Da Nang", CountryCode = "VN", IsDefault = true };
            return new Shopper(user, product, cart, address);
        }
        var alice = NewShopper();
        var bob = NewShopper();
        var voucher = new Voucher { Code = $"CVE-{Guid.NewGuid():N}".ToUpperInvariant(), DiscountType = DiscountType.Percentage,
            DiscountValue = 30m, MaxDiscountAmount = 120000m, MinOrderAmount = 300000m,
            UsageLimit = 10, PerUserLimit = 1, UsedCount = 0, IsActive = true,
            StartAt = Now.AddDays(-1), EndAt = Now.AddDays(1) };
        Guid[] userIds = [alice.User.Id, bob.User.Id];
        Guid[] cartIds = [alice.Cart.Id, bob.Cart.Id];
        Guid[] productIds = [alice.Product.Id, bob.Product.Id];
        try
        {
            db.AddRange(category, voucher);
            foreach (var shopper in new[] { alice, bob })
            {
                db.AddRange(shopper.User, shopper.Product, shopper.Cart, shopper.Address);
                db.CartItems.Add(new CartItem { CartId = shopper.Cart.Id, ProductId = shopper.Product.Id, Quantity = 2 });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await action(new State(factory, db, voucher, alice, bob));
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

    private sealed record Attempt(CheckoutResultDto? Result, BusinessException? Error);
    private sealed record Shopper(User User, Product Product, Cart Cart, UserAddress Address);
    private sealed record State(TestApiFactory Factory, FlowerShopDbContext Db, Voucher Voucher, Shopper Alice, Shopper Bob);
}
