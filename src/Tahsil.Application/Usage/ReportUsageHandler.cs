using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Tahsil.Domain.Usage;
using Meter = Tahsil.Domain.Catalog.Meter;
using MeterAggregation = Tahsil.Domain.Catalog.MeterAggregation;

namespace Tahsil.Application.Usage;

/// <summary>
/// Takes a batch of usage events (USG-1 to USG-6). Each event is stored, found to be a duplicate or rejected by
/// itself; the batch fails as a whole only when it has no events or too many.
/// </summary>
public sealed class ReportUsageHandler(ITahsilDbContext context, IAuthorizer authorizer, IUsageStore store, IOptions<UsageOptions> options, BillingClock clock)
{
    public async Task<Result<ReportUsageResponse>> HandleAsync(ReportUsageRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.UsageWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var settings = options.Value;
        var reported = request.Events;
        if (reported is null || reported.Count == 0 || reported.Count > settings.MaxBatchSize || reported.Any(usage => usage is null))
        {
            return UsageErrors.BatchInvalid(settings.MaxBatchSize);
        }

        var now = clock.GetUtcNow();
        var meters = await MetersAsync(reported, cancellationToken);
        var customers = await CustomersAsync(reported, cancellationToken);

        var events = new UsageEvent?[reported.Count];
        var errors = new Error?[reported.Count];
        foreach (var (index, usage) in reported.Index())
        {
            var read = Read(usage, meters, customers, now, settings.FutureTolerance);
            if (read.IsSuccess)
            {
                events[index] = read.Value;
            }
            else
            {
                errors[index] = read.Error;
            }
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        var valid = events.OfType<UsageEvent>().ToList();
        if (valid.Count > 0)
        {
            var billed = (await store.LockBilledAsync([.. valid.Select(usage => usage.CustomerId).Distinct()], [.. valid.Select(usage => usage.MeterId).Distinct()], cancellationToken))
                .Where(item => item.BilledUntil is not null)
                .GroupBy(item => (item.CustomerId, item.MeterId))
                .ToDictionary(group => group.Key, group => group.Max(item => item.BilledUntil!.Value));
            foreach (var (index, usage) in events.Index())
            {
                if (usage is not null && billed.TryGetValue((usage.CustomerId, usage.MeterId), out var until) && usage.OccurredAt < until)
                {
                    (events[index], errors[index]) = (null, UsageErrors.PeriodClosed);
                }
            }
        }

        // A repeated report is a duplicate whatever it says now (USG-2), so a retry after its period closed is not told the event was refused.
        var refusedIdentifiers = reported.Where((usage, index) => errors[index] is not null && UsageEvent.IsValidIdentifier(usage.Id)).Select(usage => usage.Id).Distinct().ToList();
        var known = refusedIdentifiers.Count == 0 ? [] : (await store.ExistingAsync(refusedIdentifiers, cancellationToken)).ToHashSet(StringComparer.Ordinal);

        valid = [.. events.OfType<UsageEvent>()];
        var stored = valid.Count == 0 ? [] : (await store.InsertAsync(valid, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        await transaction.CommitAsync(cancellationToken);

        var accepted = stored.Count;
        var duplicates = new List<string>();
        var rejected = new List<RejectedUsageEvent>();
        foreach (var (index, usage) in reported.Index())
        {
            if (errors[index] is { } error)
            {
                if (known.Contains(usage.Id))
                {
                    duplicates.Add(usage.Id);
                }
                else
                {
                    rejected.Add(new RejectedUsageEvent(usage.Id ?? string.Empty, index, error.Code, error.Message));
                }
            }
            else if (!stored.Remove(usage.Id))
            {
                // Stored before, or earlier in this batch.
                duplicates.Add(usage.Id);
            }
        }

        return new ReportUsageResponse(accepted, duplicates, rejected);
    }

    private static Result<UsageEvent> Read(
        UsageEventRequest usage, Dictionary<string, Meter> meters, CustomerIndex customers, DateTimeOffset now, TimeSpan futureTolerance)
    {
        if (!UsageEvent.IsValidIdentifier(usage.Id))
        {
            return UsageErrors.InvalidIdentifier;
        }

        if (string.IsNullOrEmpty(usage.Meter))
        {
            return UsageErrors.InvalidMeter;
        }

        if ((usage.Customer is null) == (usage.CustomerExternalId is null))
        {
            return UsageErrors.InvalidCustomer;
        }

        if (!meters.TryGetValue(usage.Meter, out var meter))
        {
            return UsageErrors.MeterNotFound;
        }

        if (meter.IsArchived)
        {
            return UsageErrors.MeterArchived;
        }

        if (customers.Find(usage) is not { } customerId)
        {
            return UsageErrors.CustomerNotFound;
        }

        // Meters that count events ignore the value.
        var value = meter.Aggregation == MeterAggregation.Count ? 1 : usage.Value ?? 1;
        if (!UsageEvent.IsValidValue(value))
        {
            return UsageErrors.InvalidValue;
        }

        var occurredAt = usage.Timestamp ?? now;
        if (occurredAt > now + futureTolerance)
        {
            return UsageErrors.FutureTimestamp;
        }

        string? properties = null;
        if (usage.Properties is { Count: > 0 } given)
        {
            properties = JsonSerializer.Serialize(given);
            if (Encoding.UTF8.GetByteCount(properties) > UsageEvent.PropertiesMaxBytes)
            {
                return UsageErrors.InvalidProperties;
            }
        }

        if (meter.UniqueProperty is { } counted
            && !(usage.Properties?.TryGetValue(counted, out var distinct) == true
                && distinct.ValueKind == JsonValueKind.String
                && distinct.GetString() is { Length: > 0 and <= UsageEvent.UniqueValueMaxLength }))
        {
            return UsageErrors.MissingUniqueProperty(counted);
        }

        return UsageEvent.Create(usage.Id, meter.Id, customerId, value, occurredAt, properties, now);
    }

    private async Task<Dictionary<string, Meter>> MetersAsync(IReadOnlyList<UsageEventRequest> reported, CancellationToken cancellationToken)
    {
        var keys = reported.Select(usage => usage.Meter).Where(key => !string.IsNullOrEmpty(key)).Distinct().ToList();
        return await context.Meters.AsNoTracking().Where(meter => keys.Contains(meter.Key)).ToDictionaryAsync(meter => meter.Key, StringComparer.Ordinal, cancellationToken);
    }

    private async Task<CustomerIndex> CustomersAsync(IReadOnlyList<UsageEventRequest> reported, CancellationToken cancellationToken)
    {
        var ids = reported.Where(usage => usage.Customer is not null).Select(usage => usage.Customer!.Value).Distinct().ToList();
        var externalIds = reported.Where(usage => usage.CustomerExternalId is not null).Select(usage => usage.CustomerExternalId!).Distinct().ToList();

        // Anonymized customers take no more usage (USG-4).
        var found = await context.Customers.AsNoTracking()
            .Where(customer => customer.AnonymizedAt == null && (ids.Contains(customer.Id) || (customer.ExternalId != null && externalIds.Contains(customer.ExternalId))))
            .Select(customer => new { customer.Id, customer.ExternalId })
            .ToListAsync(cancellationToken);
        return new CustomerIndex(
            [.. found.Select(customer => customer.Id)],
            found.Where(customer => customer.ExternalId is not null).ToDictionary(customer => customer.ExternalId!, customer => customer.Id, StringComparer.Ordinal));
    }

    private sealed record CustomerIndex(HashSet<Guid> Ids, Dictionary<string, Guid> ByExternalId)
    {
        public Guid? Find(UsageEventRequest usage) =>
            usage.Customer is { } id ? (Ids.Contains(id) ? id : null)
            : ByExternalId.TryGetValue(usage.CustomerExternalId!, out var found) ? found
            : null;
    }
}
