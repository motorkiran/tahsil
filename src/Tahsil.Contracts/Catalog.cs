using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tahsil.Contracts;

public enum MeterAggregation
{
    Sum,
    Count,
    Max,
    UniqueCount,
}

public enum PriceType
{
    OneTime,
    Recurring,
}

public enum PriceInterval
{
    Day,
    Week,
    Month,
    Year,
}

public enum UsageType
{
    Licensed,
    Metered,
}

public enum BillingScheme
{
    PerUnit,
    Tiered,
    Package,
}

public enum PackageRounding
{
    Up,
    Down,
}

public enum TiersMode
{
    Graduated,
    Volume,
}

/// <summary>A tier of a tiered price: units up to <c>upTo</c>, unbounded for the last tier.</summary>
public sealed record PriceTier(long? UpTo, long? UnitAmount = null, decimal? UnitAmountDecimal = null, long? FlatAmount = null);

public sealed record ProductSummary(Guid Id, string Key, string Name);

public sealed record TaxRateSummary(Guid Id, string Key, string Name, decimal Percentage, bool Inclusive);

public sealed record MeterSummary(Guid Id, string Key, string Name);

public sealed record TaxRateResponse(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    decimal Percentage,
    bool Inclusive,
    string? Country,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateTaxRateRequest
{
    [Required]
    public required string Key { get; init; }

    [Required]
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Between 0 and 100, with up to four decimals, such as 20.</summary>
    public required decimal Percentage { get; init; }

    /// <summary>Whether prices using the rate include the tax.</summary>
    public required bool Inclusive { get; init; }

    /// <summary>ISO 3166-1 alpha-2, such as <c>TR</c>; informational.</summary>
    public string? Country { get; init; }
}

/// <summary>The percentage and inclusion never change; create a new rate instead.</summary>
public sealed record UpdateTaxRateRequest
{
    public string? Name { get; init; }

    public string? Description
    {
        get;
        init
        {
            field = value;
            HasDescription = true;
        }
    }

    public bool? IsArchived { get; init; }

    [JsonIgnore]
    public bool HasDescription { get; private init; }
}

public sealed record MeterResponse(
    Guid Id,
    string Key,
    string Name,
    MeterAggregation Aggregation,
    string? UniqueProperty,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateMeterRequest
{
    [Required]
    public required string Key { get; init; }

    [Required]
    public required string Name { get; init; }

    public required MeterAggregation Aggregation { get; init; }

    /// <summary>For <c>unique_count</c>: the event property whose distinct values are counted.</summary>
    public string? UniqueProperty { get; init; }
}

public sealed record UpdateMeterRequest
{
    public string? Name { get; init; }

    public bool? IsArchived { get; init; }
}

public sealed record ProductResponse(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string? KimlikPlan,
    bool IsArchived,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateProductRequest
{
    [Required]
    public required string Key { get; init; }

    [Required]
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The key of the Kimlik plan a subscription to this product grants.</summary>
    public string? KimlikPlan { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>JSON Merge Patch: omitted fields stay as they are, <c>null</c> clears.</summary>
public sealed record UpdateProductRequest
{
    public string? Name { get; init; }

    public string? Description
    {
        get;
        init
        {
            field = value;
            HasDescription = true;
        }
    }

    public string? KimlikPlan
    {
        get;
        init
        {
            field = value;
            HasKimlikPlan = true;
        }
    }

    /// <summary>Archived products sell no more; existing subscriptions keep renewing.</summary>
    public bool? IsArchived { get; init; }

    /// <summary>Replaces the metadata; <c>null</c> clears it.</summary>
    public IReadOnlyDictionary<string, string>? Metadata
    {
        get;
        init
        {
            field = value;
            HasMetadata = true;
        }
    }

    [JsonIgnore]
    public bool HasDescription { get; private init; }

    [JsonIgnore]
    public bool HasKimlikPlan { get; private init; }

    [JsonIgnore]
    public bool HasMetadata { get; private init; }
}

public sealed record PriceResponse(
    Guid Id,
    string? Key,
    string? Name,
    ProductSummary Product,
    string Currency,
    PriceType Type,
    PriceInterval? Interval,
    int? IntervalCount,
    UsageType? UsageType,
    BillingScheme BillingScheme,
    long? UnitAmount,
    decimal? UnitAmountDecimal,
    int? PackageSize,
    PackageRounding? PackageRounding,
    TiersMode? TiersMode,
    IReadOnlyList<PriceTier>? Tiers,
    MeterSummary? Meter,
    int? TrialDays,
    TaxRateSummary? TaxRate,
    bool IsArchived,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreatePriceRequest
{
    /// <summary>The product's ID or key.</summary>
    [Required]
    public required string Product { get; init; }

    /// <summary>A unique lookup key, such as <c>pro-monthly-try</c>.</summary>
    public string? Key { get; init; }

    /// <summary>Moves <see cref="Key"/> from the price that holds it to this one.</summary>
    public bool TransferKey { get; init; }

    public string? Name { get; init; }

    [Required]
    public required string Currency { get; init; }

    public required PriceType Type { get; init; }

    public PriceInterval? Interval { get; init; }

    /// <summary>1 by default for recurring prices.</summary>
    public int? IntervalCount { get; init; }

    /// <summary><c>licensed</c> by default for recurring prices.</summary>
    public UsageType? UsageType { get; init; }

    public BillingScheme BillingScheme { get; init; } = BillingScheme.PerUnit;

    /// <summary>In minor units, such as 29900 for ₺299.00.</summary>
    public long? UnitAmount { get; init; }

    /// <summary>Fractional minor units, for metered prices below one minor unit.</summary>
    public decimal? UnitAmountDecimal { get; init; }

    public int? PackageSize { get; init; }

    public PackageRounding? PackageRounding { get; init; }

    public TiersMode? TiersMode { get; init; }

    public IReadOnlyList<PriceTier>? Tiers { get; init; }

    /// <summary>The meter's ID or key, for metered prices.</summary>
    public string? Meter { get; init; }

    /// <summary>The tax rate's ID or key; untaxed when omitted.</summary>
    public string? TaxRate { get; init; }

    public int? TrialDays { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// JSON Merge Patch of what may change: the lookup key, name, trial, archive flag and metadata. Anything else is
/// refused with <c>price.immutable</c>: a new amount is a new price.
/// </summary>
public sealed record UpdatePriceRequest
{
    public string? Key
    {
        get;
        init
        {
            field = value;
            HasKey = true;
        }
    }

    /// <summary>Moves <see cref="Key"/> from the price that holds it to this one.</summary>
    public bool TransferKey { get; init; }

    public string? Name
    {
        get;
        init
        {
            field = value;
            HasName = true;
        }
    }

    public int? TrialDays
    {
        get;
        init
        {
            field = value;
            HasTrialDays = true;
        }
    }

    public bool? IsArchived { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata
    {
        get;
        init
        {
            field = value;
            HasMetadata = true;
        }
    }

    /// <summary>Any other member, such as an attempt to change the amount.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; init; }

    [JsonIgnore]
    public bool HasKey { get; private init; }

    [JsonIgnore]
    public bool HasName { get; private init; }

    [JsonIgnore]
    public bool HasTrialDays { get; private init; }

    [JsonIgnore]
    public bool HasMetadata { get; private init; }
}
