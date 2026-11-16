
using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using AuthDemo.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthDemo.Services;

public sealed class HospitalPaymentRecoveryLockService
{
    private readonly AppDbContext _db;

    public HospitalPaymentRecoveryLockService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PaymentRecoveryLockHandle?> TryAcquireAsync(
        int hospitalId,
        string gatewayPaymentId,
        CancellationToken cancellationToken = default)
    {
        if (hospitalId <= 0)
            throw new ArgumentOutOfRangeException(nameof(hospitalId));

        if (string.IsNullOrWhiteSpace(gatewayPaymentId))
            throw new ArgumentException(
                "Gateway payment ID is required.",
                nameof(gatewayPaymentId));

        // The lock belongs to this database transaction.
        // Other workers cannot acquire the same lock until
        // this transaction commits or rolls back.
        var transaction = await _db.Database
            .BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        try
        {
            var connection = _db.Database.GetDbConnection();

            await using var command = connection.CreateCommand();

            command.Transaction =
                transaction.GetDbTransaction();

            command.CommandText = @"
DECLARE @result INT;

EXEC @result = sys.sp_getapplock
    @Resource = @resource,
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = 0;

SELECT @result;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";

            // SQL Server application lock resources
            // are limited to 255 characters.
            parameter.Value =
                $"HospitalPaymentRecovery:{hospitalId}:{gatewayPaymentId}";

            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(
                cancellationToken);

            var lockResult = Convert.ToInt32(result);

            if (lockResult < 0)
            {
                await transaction.RollbackAsync(
                    cancellationToken);

                await transaction.DisposeAsync();

                return null;
            }

            return new PaymentRecoveryLockHandle(transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}

public sealed class PaymentRecoveryLockHandle : IAsyncDisposable
{
    private readonly IDbContextTransaction _transaction;
    private bool _completed;

    internal PaymentRecoveryLockHandle(
        IDbContextTransaction transaction)
    {
        _transaction = transaction;
    }

    public async Task CommitAsync(
        CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException(
                "Transaction already completed.");

        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
    }
}
