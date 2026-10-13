using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Tahsil.Contracts;

public sealed record ReportUsageRequest
{
    /// <summary>The events, 1,000 at most unless the installation allows another number.</summary>
    [Required]
    public required IReadOnlyList<UsageEventRequest> Events { get; init; }
}

/// <summary>One report of usage. The customer is named by <c>customer</c> or by <c>customerExternalId</c>.</summary>
public sealed record UsageEventRequest
{
    /// <summary>Your ID of the event, 1 to 100 letters, digits, <c>.</c>, <c>_</c>, <c>:</c> and <c>-</c>. Send the same ID when you retry: an ID is stored once.</summary>
    [Required]
    public required string Id { get; init; }

    public Guid? Customer { get; init; }

    public string? CustomerExternalId { get; init; }

    /// <summary>The meter's key.</summary>
    [Required]
    public required string Meter { get; init; }

    /// <summary>1 by default; not negative, with at most eight decimals. Meters that count events ignore it.</summary>
    public decimal? Value { get; init; }

    /// <summary>When the usage happened; now by default.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>An object of at most 4 KB. A meter that counts distinct values needs its property here, as a string.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }
}

/// <summary>What became of a batch: every event is accepted, a duplicate or rejected by itself.</summary>
public sealed record ReportUsageResponse(int Accepted, IReadOnlyList<string> Duplicates, IReadOnlyList<RejectedUsageEvent> Rejected);

/// <summary>An event that was not stored, by its ID and its place in the batch (from 0).</summary>
public sealed record RejectedUsageEvent(string Id, int Index, string Code, string Message);

public enum UsageInterval
{
    Day,
    Month,
}

/// <summary>
/// A customer's usage on a meter over [<c>from</c>, <c>to</c>): the quantity of the whole range, and of each day or
/// month in the billing time zone, each aggregated the meter's way.
/// </summary>
public sealed record CustomerUsageResponse(
    MeterSummary Meter, DateTimeOffset From, DateTimeOffset To, UsageInterval Interval, decimal Quantity, IReadOnlyList<UsageBucketResponse> Buckets);

public sealed record UsageBucketResponse(DateTimeOffset Start, DateTimeOffset End, decimal Quantity);

public sealed record SubscriptionUsageResponse(IReadOnlyList<SubscriptionItemUsageResponse> Items);

/// <summary>
/// A metered item's usage in the subscription's current period so far, and the amount it would bill now, before
/// discounts and tax that is added; 0 during a trial, whose usage is free.
/// </summary>
public sealed record SubscriptionItemUsageResponse(
    Guid Item, SubscriptionPriceResponse Price, MeterSummary Meter, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, decimal Quantity, long Amount, string Currency);
