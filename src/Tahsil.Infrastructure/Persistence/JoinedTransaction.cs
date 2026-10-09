using Microsoft.EntityFrameworkCore.Storage;

namespace Tahsil.Infrastructure.Persistence;

/// <summary>
/// Stands for a transaction that is already in progress: committing or disposing it does nothing, as the code
/// that started the transaction decides its outcome.
/// </summary>
internal sealed class JoinedTransaction(IDbContextTransaction outer) : IDbContextTransaction
{
    public Guid TransactionId => outer.TransactionId;

    public void Commit()
    {
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Rollback()
    {
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
