using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Usage;
using Tahsil.Domain.Usage;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Usage;

/// <summary>Usage events in PostgreSQL, a batch per statement.</summary>
internal sealed class UsageStore(TahsilDbContext context) : IUsageStore
{
    public async Task<IReadOnlyList<UsageBilled>> LockBilledAsync(IReadOnlyCollection<Guid> customerIds, IReadOnlyCollection<Guid> meterIds, CancellationToken cancellationToken)
    {
        var customers = customerIds.ToArray();
        var meters = meterIds.ToArray();

        // Invoicing updates the items it bills, which waits for these locks, and these wait for it.
        return await context.Database.SqlQuery<UsageBilled>($"""
            SELECT s.customer_id, p.meter_id, i.usage_billed_until AS billed_until
            FROM tahsil.subscription_items i
            JOIN tahsil.subscriptions s ON s.id = i.subscription_id
            JOIN tahsil.prices p ON p.id = i.price_id
            WHERE s.customer_id = ANY({customers}) AND p.meter_id = ANY({meters})
            ORDER BY i.id
            FOR SHARE OF i
            """).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> InsertAsync(IReadOnlyList<UsageEvent> events, CancellationToken cancellationToken)
    {
        var ids = events.Select(usage => usage.Id).ToArray();
        var identifiers = events.Select(usage => usage.Identifier).ToArray();
        var meters = events.Select(usage => usage.MeterId).ToArray();
        var customers = events.Select(usage => usage.CustomerId).ToArray();
        var values = events.Select(usage => usage.Value).ToArray();
        var occurred = events.Select(usage => usage.OccurredAt).ToArray();
        var properties = events.Select(usage => usage.Properties).ToArray();
        var received = events.Select(usage => usage.ReceivedAt).ToArray();

        return await context.Database.SqlQuery<string>($"""
            INSERT INTO tahsil.usage_events (id, identifier, meter_id, customer_id, value, occurred_at, properties, received_at)
            SELECT e.id, e.identifier, e.meter_id, e.customer_id, e.value, e.occurred_at, e.properties::jsonb, e.received_at
            FROM unnest({ids}, {identifiers}, {meters}, {customers}, {values}, {occurred}, {properties}, {received})
                AS e(id, identifier, meter_id, customer_id, value, occurred_at, properties, received_at)
            ON CONFLICT (identifier) DO NOTHING
            RETURNING identifier AS "Value"
            """).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> ExistingAsync(IReadOnlyCollection<string> identifiers, CancellationToken cancellationToken)
    {
        var asked = identifiers.ToArray();
        return await context.UsageEvents.AsNoTracking().Where(usage => asked.Contains(usage.Identifier)).Select(usage => usage.Identifier).ToListAsync(cancellationToken);
    }
}
