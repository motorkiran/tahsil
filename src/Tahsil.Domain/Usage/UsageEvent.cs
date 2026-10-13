using Tahsil.Domain.Common;

namespace Tahsil.Domain.Usage;

/// <summary>
/// One report of a customer's usage on a meter (USG-1). The identifier is the application's, and makes a repeated
/// report harmless. Events are append-only: only retention deletes them.
/// </summary>
public sealed class UsageEvent
{
    public const int IdentifierMaxLength = 100;
    public const int PropertiesMaxBytes = 4096;
    public const int UniqueValueMaxLength = 200;

    /// <summary>The decimals a value keeps.</summary>
    public const int ValueScale = 8;

    /// <summary>Values stay below this, the most the column holds.</summary>
    public const decimal ValueLimit = 100_000_000_000_000_000_000m;

    private UsageEvent()
    {
    }

    public Guid Id { get; private init; }

    public string Identifier { get; private init; } = string.Empty;

    public Guid MeterId { get; private init; }

    public Guid CustomerId { get; private init; }

    public decimal Value { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    /// <summary>The event's properties, a JSON object.</summary>
    public string? Properties { get; private init; }

    public DateTimeOffset ReceivedAt { get; private init; }

    public static UsageEvent Create(string identifier, Guid meterId, Guid customerId, decimal value, DateTimeOffset occurredAt, string? properties, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Identifier = identifier,
        MeterId = meterId,
        CustomerId = customerId,
        Value = value,
        OccurredAt = occurredAt.ToUniversalTime(),
        Properties = properties,
        ReceivedAt = now,
    };

    /// <summary>1 to 100 letters, digits, <c>.</c>, <c>_</c>, <c>:</c> and <c>-</c>.</summary>
    public static bool IsValidIdentifier(string? identifier) =>
        identifier is { Length: > 0 and <= IdentifierMaxLength } && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    /// <summary>Not negative, below <see cref="ValueLimit"/>, with at most <see cref="ValueScale"/> decimals.</summary>
    public static bool IsValidValue(decimal value) => value is >= 0 and < ValueLimit && decimal.Round(value, ValueScale) == value;
}

public static class UsageErrors
{
    public static readonly Error InvalidIdentifier = Error.Validation(
        "usage_event.invalid", "An event's id is 1 to 100 letters, digits, '.', '_', ':' and '-'.", "id");

    public static readonly Error InvalidCustomer = Error.Validation(
        "usage_event.invalid", "An event names its customer by 'customer' or by 'customerExternalId', not both.", "customer");

    public static readonly Error InvalidMeter = Error.Validation("usage_event.invalid", "An event names its meter by key.", "meter");

    public static readonly Error InvalidValue = Error.Validation(
        "usage_event.invalid", "A value is not negative, is below 10^20 and has at most eight decimals.", "value");

    public static readonly Error InvalidProperties = Error.Validation(
        "usage_event.invalid", "An event's properties take at most 4 KB.", "properties");

    public static readonly Error MeterNotFound = Error.Validation("usage_event.meter_not_found", "No meter has this key.", "meter");

    public static readonly Error MeterArchived = Error.Validation("usage_event.meter_archived", "The meter is archived and takes no more events.", "meter");

    public static readonly Error CustomerNotFound = Error.Validation("usage_event.customer_not_found", "The customer does not exist.", "customer");

    public static readonly Error FutureTimestamp = Error.Validation("usage_event.future_timestamp", "The timestamp is in the future.", "timestamp");

    public static readonly Error PeriodClosed = Error.Validation(
        "usage_event.period_closed", "The usage of the timestamp's period is already invoiced.", "timestamp");

    public static Error BatchInvalid(int maxBatchSize) =>
        Error.Validation("usage.batch_invalid", $"A batch has 1 to {maxBatchSize} events.", "events");

    public static Error MissingUniqueProperty(string property) => Error.Validation(
        "usage_event.missing_unique_property", $"The meter counts distinct values of '{property}': properties need it as a string of 1 to {UsageEvent.UniqueValueMaxLength} characters.", "properties");
}
