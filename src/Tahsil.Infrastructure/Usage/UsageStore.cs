using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Usage;
using Tahsil.Domain.Catalog;
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

    public async Task<IReadOnlyList<UsageCursor>> LockItemsAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<UsageCursor>($"""
            SELECT id AS item_id, usage_billed_until AS billed_until
            FROM tahsil.subscription_items
            WHERE subscription_id = {subscriptionId}
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(cancellationToken);

    public async Task<decimal> AggregateAsync(Guid customerId, Meter meter, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var events = context.UsageEvents.AsNoTracking()
            .Where(usage => usage.MeterId == meter.Id && usage.CustomerId == customerId && usage.OccurredAt >= from && usage.OccurredAt < until);
        switch (meter.Aggregation)
        {
            case MeterAggregation.Sum:
                return await events.SumAsync(usage => (decimal?)usage.Value, cancellationToken) ?? 0;
            case MeterAggregation.Count:
                return await events.LongCountAsync(cancellationToken);
            case MeterAggregation.Max:
                return await events.MaxAsync(usage => (decimal?)usage.Value, cancellationToken) ?? 0;
            default:
                var (meterId, property) = (meter.Id, meter.UniqueProperty!);
                return (await context.Database.SqlQuery<long>($"""
                    SELECT count(DISTINCT properties->>{property}) AS "Value"
                    FROM tahsil.usage_events
                    WHERE meter_id = {meterId} AND customer_id = {customerId} AND occurred_at >= {from} AND occurred_at < {until}
                    """).ToListAsync(cancellationToken)).Single();
        }
    }

    public async Task<IReadOnlyCollection<string>> ExistingAsync(IReadOnlyCollection<string> identifiers, CancellationToken cancellationToken)
    {
        var asked = identifiers.ToArray();
        return await context.UsageEvents.AsNoTracking().Where(usage => asked.Contains(usage.Identifier)).Select(usage => usage.Identifier).ToListAsync(cancellationToken);
    }
}
