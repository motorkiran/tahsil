using Tahsil.Domain.Common;

namespace Tahsil.Domain.Catalog;

/// <summary>How a meter turns usage events into a quantity for a period.</summary>
public enum MeterAggregation
{
    Sum,
    Count,
    Max,
    UniqueCount,
}

/// <summary>
/// What metered prices bill: the key names it in usage events, and the aggregation turns a period's events into a
/// quantity. The key, aggregation and counted property never change.
/// </summary>
public sealed class Meter : AggregateRoot
{
    public const int NameMaxLength = 100;
    public const int UniquePropertyMaxLength = 40;

    private Meter()
    {
    }

    public Guid Id { get; private init; }

    public string Key { get; private init; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public MeterAggregation Aggregation { get; private init; }

    /// <summary>For <see cref="MeterAggregation.UniqueCount"/>: the event property whose distinct values are counted.</summary>
    public string? UniqueProperty { get; private init; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Meter> Create(string key, string name, MeterAggregation aggregation, string? uniqueProperty, DateTimeOffset now)
    {
        if (!CatalogKey.IsValid(key))
        {
            return CatalogKey.Invalid;
        }

        if (!Text.FitsRequired(name, NameMaxLength))
        {
            return MeterErrors.InvalidName;
        }

        var property = Text.Normalize(uniqueProperty);
        if ((aggregation == MeterAggregation.UniqueCount) != (property is not null) || property?.Length > UniquePropertyMaxLength)
        {
            return MeterErrors.InvalidUniqueProperty;
        }

        return new Meter
        {
            Id = Guid.CreateVersion7(now),
            Key = key,
            Name = name.Trim(),
            Aggregation = aggregation,
            UniqueProperty = property,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public Result Update(string name, bool isArchived, DateTimeOffset now)
    {
        if (!Text.FitsRequired(name, NameMaxLength))
        {
            return MeterErrors.InvalidName;
        }

        Name = name.Trim();
        IsArchived = isArchived;
        UpdatedAt = now;
        return Result.Success();
    }
}
