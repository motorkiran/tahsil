using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Outbox;

/// <summary>Wakes the dispatcher of this instance once newly saved outbox messages are visible to it.</summary>
internal sealed class OutboxSignalInterceptor(WorkerSignals signals) : CommittedChangeInterceptor
{
    protected override bool HasChangesOfInterest(ChangeTracker changeTracker) =>
        changeTracker.Entries<OutboxMessage>().Any(entry => entry.State == EntityState.Added);

    protected override void OnVisible() => signals.Notify(OutboxDispatcher.WorkerName);
}
