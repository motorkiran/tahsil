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

    /// <summary>Which of the identifiers are stored already.</summary>
    Task<IReadOnlyCollection<string>> ExistingAsync(IReadOnlyCollection<string> identifiers, CancellationToken cancellationToken);
}

/// <summary>A subscription item's meter and customer, and the end of the last usage period it invoiced.</summary>
public sealed record UsageBilled(Guid CustomerId, Guid MeterId, DateTimeOffset? BilledUntil);
