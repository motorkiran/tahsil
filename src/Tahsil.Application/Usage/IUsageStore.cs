using Tahsil.Domain.Catalog;
using Tahsil.Domain.Usage;

namespace Tahsil.Application.Usage;

/// <summary>Usage events at volume: statements over whole batches, in the transaction of the scope's database context.</summary>
public interface IUsageStore
{
    /// <summary>
    /// Until when the usage of these customers on these meters is invoiced, by the subscription items that bill it.
    /// The items stay locked against invoicing until the transaction ends, so an event stored after this answer is
    /// either counted by the invoice of its period or was refused here (USG-5, USG-12).
    /// </summary>
    Task<IReadOnlyList<UsageBilled>> LockBilledAsync(IReadOnlyCollection<Guid> customerIds, IReadOnlyCollection<Guid> meterIds, CancellationToken cancellationToken);

    /// <summary>Stores the events whose identifiers are new, in one statement, and returns those identifiers (USG-2, USG-6).</summary>
    Task<IReadOnlyCollection<string>> InsertAsync(IReadOnlyList<UsageEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// Locks a subscription's items against usage reporting until the transaction ends, and returns until when each is
    /// invoiced, as it is now. Whoever invoices usage calls this before it counts (USG-12).
    /// </summary>
    Task<IReadOnlyList<UsageCursor>> LockItemsAsync(Guid subscriptionId, CancellationToken cancellationToken);

    /// <summary>A customer's quantity on a meter over [<paramref name="from"/>, <paramref name="until"/>) (USG-8).</summary>
    Task<decimal> AggregateAsync(Guid customerId, Meter meter, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>Which of the identifiers are stored already.</summary>
    Task<IReadOnlyCollection<string>> ExistingAsync(IReadOnlyCollection<string> identifiers, CancellationToken cancellationToken);
}

/// <summary>A subscription item's meter and customer, and the end of the last usage period it invoiced.</summary>
public sealed record UsageBilled(Guid CustomerId, Guid MeterId, DateTimeOffset? BilledUntil);

/// <summary>A subscription item and the end of the last usage period it invoiced.</summary>
public sealed record UsageCursor(Guid ItemId, DateTimeOffset? BilledUntil);
