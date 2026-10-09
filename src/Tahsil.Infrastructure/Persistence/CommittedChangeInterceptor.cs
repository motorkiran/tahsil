using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tahsil.Infrastructure.Persistence;

/// <summary>
/// Reports saved changes of interest once other connections can see them: right after the save, or after the
/// commit when the save ran inside an explicit transaction. One instance per context.
/// </summary>
internal abstract class CommittedChangeInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private bool _saving;
    private bool _awaitingCommit;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        OnSaving(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        OnSaving(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        OnSaved();
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        OnSaved();
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => OnCommitted();

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        OnCommitted();
        return Task.CompletedTask;
    }

    /// <summary>Whether the pending changes include something to report.</summary>
    protected abstract bool HasChangesOfInterest(ChangeTracker changeTracker);

    /// <summary>Reports the changes; called once they are visible to other connections.</summary>
    protected abstract void OnVisible();

    private void OnSaving(DbContext? context)
    {
        if (context is null || !HasChangesOfInterest(context.ChangeTracker))
        {
            return;
        }

        if (context.Database.CurrentTransaction is null)
        {
            _saving = true;
        }
        else
        {
            _awaitingCommit = true;
        }
    }

    private void OnSaved()
    {
        if (_saving)
        {
            _saving = false;
            OnVisible();
        }
    }

    private void OnCommitted()
    {
        if (_awaitingCommit)
        {
            _awaitingCommit = false;
            OnVisible();
        }
    }
}
