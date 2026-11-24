using System.Data;
using FlowerShop.Domain.Common;
using FlowerShop.Domain.Interfaces;
using FlowerShop.Domain.Interfaces.Repositories;
using FlowerShop.Infrastructure.Data;
using FlowerShop.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlowerShop.Infrastructure.UnitOfWork;

public sealed class UnitOfWork(FlowerShopDbContext context) : IUnitOfWork
{
    // Get repository for an entity.
    public IGenericRepository<T> GetRepository<T>()
        where T : BaseEntity
        => new GenericRepository<T>(context);

    // Save all tracked changes.
    public Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
        => context.SaveChangesAsync(cancellationToken);

    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(operation, null, cancellationToken);

    public Task<TResult> ExecuteInLockedTransactionAsync<TResult>(
        string resource,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ValidateResource(resource);
        return ExecuteCoreAsync(operation, resource, cancellationToken);
    }

    // Reuse the existing transaction and connection. Do not start or commit a transaction here.
    public Task AcquireTransactionLockAsync(
        string resource,
        CancellationToken cancellationToken = default)
    {
        ValidateResource(resource);
        return AcquireLockAsync(resource, cancellationToken);
    }

    private static void ValidateResource(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (resource.Length > 255)
            throw new ArgumentOutOfRangeException(nameof(resource));
    }

    private async Task<TResult> ExecuteCoreAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        string? resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("NESTED_TRANSACTION_NOT_SUPPORTED");

        await using var transaction =
            await context.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

        if (resource is not null)
            await AcquireLockAsync(resource, cancellationToken);

        var result = await operation(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task AcquireLockAsync(
        string resource,
        CancellationToken cancellationToken)
    {
        var transaction = context.Database.CurrentTransaction
            ?? throw new InvalidOperationException("TRANSACTION_REQUIRED");

        cancellationToken.ThrowIfCancellationRequested();
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();

        command.CommandText =
            """
            DECLARE @result int;

            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000,
                @DbPrincipal = 'public';

            SELECT @result;
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Size = 255;
        parameter.Value = resource;
        command.Parameters.Add(parameter);

        var rawResult = await command.ExecuteScalarAsync(cancellationToken);
        if (rawResult is not int result)
            throw new InvalidOperationException("APP_LOCK_INVALID_RESULT");

        // SQL Server: 0 = acquired immediately; 1 = acquired after waiting.
        if (result >= 0)
            return;
        if (result == -2)
            throw new OperationCanceledException("APP_LOCK_CANCELLED", cancellationToken);
        if (result is -1 or -3)
            throw new ResourceLockException(result);
        throw new InvalidOperationException($"APP_LOCK_FAILED_{result}");
    }
}
