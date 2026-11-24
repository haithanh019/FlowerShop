using System.Data;
using FlowerShop.Domain.Entities;
using FlowerShop.Domain.Enums;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Infrastructure.Data;
using FlowerShop.IntegrationTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.IntegrationTests.Infrastructure;

public sealed class AdditionalTransactionLockTests
{
    [Fact]
    public async Task Acquire_ShouldRejectInvalidResourceAndMissingTransaction()
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            uow.AcquireTransactionLockAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            uow.AcquireTransactionLockAsync(" "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            uow.AcquireTransactionLockAsync(new string('X', 256)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            uow.AcquireTransactionLockAsync(Resource("voucher")));
        Assert.Equal("TRANSACTION_REQUIRED", ex.Message);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task AdditionalLock_ShouldShareTransactionAndReleaseBothOnCommit()
    {
        using var factory = new TestApiFactory(Connection());
        using var owner = factory.Services.CreateScope();
        using var observer = factory.Services.CreateScope();
        var uow = owner.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var ownerDb = owner.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var observerDb = observer.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var cart = Resource("cart");
        var voucher = Resource("voucher");
        await using var observerTransaction = await observerDb.Database.BeginTransactionAsync();

        var result = await uow.ExecuteInLockedTransactionAsync(cart, async ct =>
        {
            var transaction = ownerDb.Database.CurrentTransaction;
            Assert.NotNull(transaction);
            Assert.Equal("Exclusive", await ReadModeAsync(ownerDb, cart));
            Assert.Equal("NoLock", await ReadModeAsync(ownerDb, voucher));
            await uow.AcquireTransactionLockAsync(voucher, ct);
            Assert.Same(transaction, ownerDb.Database.CurrentTransaction);
            Assert.Equal("Exclusive", await ReadModeAsync(ownerDb, cart));
            Assert.Equal("Exclusive", await ReadModeAsync(ownerDb, voucher));
            // Independent connection: both resources must be unavailable, without waiting.
            Assert.Equal(0, await CanAcquireAsync(observerDb, cart));
            Assert.Equal(0, await CanAcquireAsync(observerDb, voucher));
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Null(ownerDb.Database.CurrentTransaction);
        Assert.Equal(1, await CanAcquireAsync(observerDb, cart));
        Assert.Equal(1, await CanAcquireAsync(observerDb, voucher));
        await observerTransaction.RollbackAsync();
    }

    [Fact]
    public async Task AdditionalLock_ShouldReleaseOnRollbackAndUndoSavedChanges()
    {
        using var factory = new TestApiFactory(Connection());
        using var owner = factory.Services.CreateScope();
        using var observer = factory.Services.CreateScope();
        var uow = owner.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var ownerDb = owner.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var observerDb = observer.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var order = Resource("order");
        var voucher = Resource("voucher");
        var user = new User
        {
            FullName = "Additional Lock Test",
            Email = $"additional-lock-{Guid.NewGuid():N}@example.invalid",
            PasswordHash = "not-used-for-login",
            Role = UserRole.Customer,
            IsActive = true
        };
        await using var observerTransaction = await observerDb.Database.BeginTransactionAsync();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                uow.ExecuteInLockedTransactionAsync<bool>(order, async ct =>
                {
                    await uow.AcquireTransactionLockAsync(voucher, ct);
                    await uow.GetRepository<User>().AddAsync(user, ct);
                    await uow.SaveChangesAsync(ct); // SQL INSERT, not COMMIT.
                    Assert.True(await ownerDb.Users.AnyAsync(x => x.Id == user.Id, ct));
                    Assert.Equal(0, await CanAcquireAsync(observerDb, order));
                    Assert.Equal(0, await CanAcquireAsync(observerDb, voucher));
                    throw new InvalidOperationException("TEST_ROLLBACK");
                }));
            Assert.Equal("TEST_ROLLBACK", ex.Message);
            Assert.Null(ownerDb.Database.CurrentTransaction);
            Assert.Equal(1, await CanAcquireAsync(observerDb, order));
            Assert.Equal(1, await CanAcquireAsync(observerDb, voucher));
            Assert.False(await observerDb.Users.AsNoTracking().AnyAsync(x => x.Id == user.Id));
        }
        finally
        {
            await observerTransaction.RollbackAsync();
            // EF's tracker is not reset by SQL rollback. Clear before any reuse.
            ownerDb.ChangeTracker.Clear();
            await ownerDb.Users.Where(x => x.Id == user.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task PlainTransaction_ShouldAllowAdditionalLockButRejectNestedTransactions()
    {
        using var factory = new TestApiFactory(Connection());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowerShopDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var resource = Resource("plain");

        await uow.ExecuteInTransactionAsync(async ct =>
        {
            var transaction = db.Database.CurrentTransaction;
            Assert.NotNull(transaction);
            await uow.AcquireTransactionLockAsync(resource, ct);
            Assert.Equal("Exclusive", await ReadModeAsync(db, resource));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                uow.AcquireTransactionLockAsync(Resource("cancelled"), cancelled.Token));
            var nested = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                uow.ExecuteInTransactionAsync(_ => Task.FromResult(true), ct));
            Assert.Equal("NESTED_TRANSACTION_NOT_SUPPORTED", nested.Message);
            var nestedLocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                uow.ExecuteInLockedTransactionAsync(Resource("nested"), _ => Task.FromResult(true), ct));
            Assert.Equal("NESTED_TRANSACTION_NOT_SUPPORTED", nestedLocked.Message);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            return true;
        });
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static async Task<string> ReadModeAsync(FlowerShopDbContext db, string resource)
        => Assert.IsType<string>(await ExecuteScalarAsync(db, resource,
            "SELECT APPLOCK_MODE(N'public', @resource, N'Transaction')"));

    private static async Task<int> CanAcquireAsync(FlowerShopDbContext db, string resource)
        => Assert.IsType<int>(await ExecuteScalarAsync(db, resource,
            "SELECT APPLOCK_TEST(N'public', @resource, N'Exclusive', N'Transaction')"));

    private static async Task<object?> ExecuteScalarAsync(
        FlowerShopDbContext db, string resource, string sql)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("TRANSACTION_REQUIRED");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Size = 255;
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync();
    }

    private static string Resource(string kind) => $"flower-shop:test:{kind}:{Guid.NewGuid():N}";

    private static string Connection() => Environment.GetEnvironmentVariable("FLOWERSHOP_TEST_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing FLOWERSHOP_TEST_CONNECTION_STRING.");
}
