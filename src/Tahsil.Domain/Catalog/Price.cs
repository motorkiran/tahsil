using Tahsil.Domain.Common;

namespace Tahsil.Domain.Catalog;

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
    /// <summary>A quantity set on the subscription, billed in advance.</summary>
    Licensed,

    /// <summary>Usage reported through a meter, billed in arrears.</summary>
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
    /// <summary>Each unit is priced by the tier it falls in.</summary>
    Graduated,

    /// <summary>Every unit is priced by the tier the total falls in.</summary>
    Volume,
}

/// <summary>A tier of a tiered price: units up to <see cref="UpTo"/> (unbounded for the last tier).</summary>
public sealed record PriceTier(long? UpTo, long? UnitAmount, decimal? UnitAmountDecimal, long? FlatAmount);

/// <summary>Everything a new price is made of, before it is validated.</summary>
public sealed record PriceDraft
{
    public string? Key { get; init; }

    public string? Name { get; init; }

    public required string Currency { get; init; }

    public required PriceType Type { get; init; }

    public PriceInterval? Interval { get; init; }

    public int? IntervalCount { get; init; }

    public UsageType? UsageType { get; init; }

    public BillingScheme BillingScheme { get; init; } = BillingScheme.PerUnit;

    public long? UnitAmount { get; init; }

    public decimal? UnitAmountDecimal { get; init; }

    public int? PackageSize { get; init; }

    public PackageRounding? PackageRounding { get; init; }

    public TiersMode? TiersMode { get; init; }

    public IReadOnlyList<PriceTier>? Tiers { get; init; }

    public int? TrialDays { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// What a product costs in one currency and, for subscriptions, one interval. Only the key, name, trial, archive
/// flag and metadata change (DOM-5): a new amount is a new price, so existing subscriptions keep their meaning.
/// </summary>
public sealed class Price : AggregateRoot
{
    public const int NameMaxLength = 100;
    public const int MaxTrialDays = 730;
    public const int UnitAmountDecimalScale = 12;

    private Price()
    {
    }

    public Guid Id { get; private init; }

    public Guid ProductId { get; private init; }

    /// <summary>A unique lookup key, such as <c>pro-monthly-try</c>, that can move to a newer price.</summary>
    public string? Key { get; private set; }

    public string? Name { get; private set; }

    public string Currency { get; private init; } = string.Empty;

    public PriceType Type { get; private init; }

    public PriceInterval? Interval { get; private init; }

    public short? IntervalCount { get; private init; }

    public UsageType? UsageType { get; private init; }

    public BillingScheme BillingScheme { get; private init; }

    public long? UnitAmount { get; private init; }

    public decimal? UnitAmountDecimal { get; private init; }

    public int? PackageSize { get; private init; }

    public PackageRounding? PackageRounding { get; private init; }

    public TiersMode? TiersMode { get; private init; }

    public IReadOnlyList<PriceTier>? Tiers { get; private init; }

    public Guid? MeterId { get; private init; }

    public short? TrialDays { get; private set; }

    public Guid? TaxRateId { get; private init; }

    public bool IsArchived { get; private set; }

    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = Common.Metadata.Empty;

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Price> Create(PriceDraft draft, Product product, TaxRate? taxRate, Meter? meter, DateTimeOffset now)
    {
        if (product.IsArchived)
        {
            return PriceErrors.InvalidReference("product", "The product is archived; it sells no more.");
        }

        if (taxRate is { IsArchived: true })
        {
            return PriceErrors.InvalidReference("taxRate", "The tax rate is archived; give new prices a current one.");
        }

        if (meter is { IsArchived: true })
        {
            return PriceErrors.InvalidReference("meter", "The meter is archived; give new prices a current one.");
        }

        if (ValidateShape(draft, meter is not null) is { } invalid)
        {
            return invalid;
        }

        var price = new Price
        {
            Id = Guid.CreateVersion7(now),
            ProductId = product.Id,
            Currency = draft.Currency,
            Type = draft.Type,
            Interval = draft.Interval,
            IntervalCount = (short?)draft.IntervalCount,
            UsageType = draft.UsageType,
            BillingScheme = draft.BillingScheme,
            UnitAmount = draft.UnitAmount,
            UnitAmountDecimal = draft.UnitAmountDecimal,
            PackageSize = draft.PackageSize,
            PackageRounding = draft.PackageRounding,
            TiersMode = draft.TiersMode,
            Tiers = draft.Tiers is null ? null : [.. draft.Tiers],
            MeterId = meter?.Id,
            TaxRateId = taxRate?.Id,
            CreatedAt = now,
        };

        var update = price.Update(draft.Key, draft.Name, draft.TrialDays, isArchived: false, draft.Metadata, now);
        return update.IsFailure ? update.Error : price;
    }

    /// <summary>Changes what may change: the lookup key, name, trial, archive flag and metadata.</summary>
    public Result Update(string? key, string? name, int? trialDays, bool isArchived, IReadOnlyDictionary<string, string>? metadata, DateTimeOffset now)
    {
        if (key is not null && !CatalogKey.IsValid(key))
        {
            return CatalogKey.Invalid;
        }

        if (!Text.FitsOptional(name, NameMaxLength))
        {
            return PriceErrors.Invalid("name", "The name is at most 100 characters.");
        }

        if (trialDays is not null && (Type != PriceType.Recurring || trialDays is < 1 or > MaxTrialDays))
        {
            return PriceErrors.Invalid("trialDays", $"Recurring prices may have a trial of 1 to {MaxTrialDays} days; one-time prices have none.");
        }

        var validMetadata = Common.Metadata.Validate(metadata);
        if (validMetadata.IsFailure)
        {
            return validMetadata.Error;
        }

        Key = key;
        Name = Text.Normalize(name);
        TrialDays = (short?)trialDays;
        IsArchived = isArchived;
        Metadata = validMetadata.Value;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Gives the lookup key up, so another price can take it (CAT-10).</summary>
    public void ReleaseKey(DateTimeOffset now)
    {
        Key = null;
        UpdatedAt = now;
    }

    /// <summary>The rules of DOM-4, in the order a person fixing a request would want them.</summary>
    private static Error? ValidateShape(PriceDraft draft, bool hasMeter)
    {
        if (!Money.Currency.TryGet(draft.Currency, out _))
        {
            return PriceErrors.UnsupportedCurrency;
        }

        if (!Enum.IsDefined(draft.Type) || !Enum.IsDefined(draft.BillingScheme))
        {
            return PriceErrors.Invalid("type", "Unknown price type or billing scheme.");
        }

        if (draft.Type == PriceType.OneTime)
        {
            if (draft.Interval is not null || draft.IntervalCount is not null)
            {
                return PriceErrors.Invalid("interval", "One-time prices have no interval.");
            }

            if (draft.UsageType is not null || hasMeter)
            {
                return PriceErrors.Invalid("usageType", "One-time prices have no usage type or meter.");
            }

            if (draft.BillingScheme != BillingScheme.PerUnit)
            {
                return PriceErrors.Invalid("billingScheme", "One-time prices are per unit.");
            }

            if (draft.UnitAmount is null || draft.UnitAmountDecimal is not null)
            {
                return PriceErrors.Invalid("unitAmount", "One-time prices have a unit amount in minor units.");
            }
        }
        else
        {
            if (draft.Interval is not { } interval || !Enum.IsDefined(interval) || draft.IntervalCount is not { } count || count < 1)
            {
                return PriceErrors.Invalid("interval", "Recurring prices have an interval and an interval count of at least 1.");
            }

            if (count > MaxCount(interval))
            {
                return PriceErrors.Invalid("intervalCount", "The interval is at most one year in total (365 days, 52 weeks or 12 months).");
            }

            if (draft.UsageType is not { } usage || !Enum.IsDefined(usage))
            {
                return PriceErrors.Invalid("usageType", "Recurring prices are licensed or metered.");
            }

            if ((usage == Catalog.UsageType.Metered) != hasMeter)
            {
                return PriceErrors.Invalid("meter", "Metered prices name a meter; licensed prices have none.");
            }
        }

        return draft.BillingScheme switch
        {
            BillingScheme.PerUnit => ValidatePerUnit(draft),
            BillingScheme.Package => ValidatePackage(draft),
            _ => ValidateTiered(draft),
        };
    }

    private static Error? ValidatePerUnit(PriceDraft draft)
    {
        if (draft.PackageSize is not null || draft.PackageRounding is not null || draft.Tiers is not null || draft.TiersMode is not null)
        {
            return PriceErrors.Invalid("billingScheme", "Per-unit prices have no packages or tiers.");
        }

        if ((draft.UnitAmount is null) == (draft.UnitAmountDecimal is null))
        {
            return PriceErrors.Invalid("unitAmount", "Per-unit prices have exactly one of unitAmount and unitAmountDecimal.");
        }

        if (draft.UnitAmountDecimal is not null && draft.UsageType != Catalog.UsageType.Metered)
        {
            return PriceErrors.Invalid("unitAmountDecimal", "Fractional unit amounts are for metered prices.");
        }

        return ValidateAmounts("unitAmount", draft.UnitAmount, draft.UnitAmountDecimal, flatAmount: null);
    }

    private static Error? ValidatePackage(PriceDraft draft)
    {
        if (draft.Tiers is not null || draft.TiersMode is not null || draft.UnitAmountDecimal is not null)
        {
            return PriceErrors.Invalid("billingScheme", "Package prices have a unit amount per package and no tiers.");
        }

        if (draft.UnitAmount is null)
        {
            return PriceErrors.Invalid("unitAmount", "Package prices have the amount of one package in unitAmount.");
        }

        if (draft.PackageSize is not >= 2)
        {
            return PriceErrors.Invalid("packageSize", "A package holds at least 2 units.");
        }

        if (draft.PackageRounding is not { } rounding || !Enum.IsDefined(rounding))
        {
            return PriceErrors.Invalid("packageRounding", "Package prices round partial packages up or down.");
        }

        return ValidateAmounts("unitAmount", draft.UnitAmount, null, flatAmount: null);
    }

    private static Error? ValidateTiered(PriceDraft draft)
    {
        if (draft.UnitAmount is not null || draft.UnitAmountDecimal is not null || draft.PackageSize is not null || draft.PackageRounding is not null)
        {
            return PriceErrors.Invalid("billingScheme", "Tiered prices carry their amounts in their tiers.");
        }

        if (draft.TiersMode is not { } mode || !Enum.IsDefined(mode))
        {
            return PriceErrors.Invalid("tiersMode", "Tiered prices are graduated or volume.");
        }

        if (draft.Tiers is not { Count: > 0 } tiers)
        {
            return PriceErrors.Invalid("tiers", "Tiered prices have at least one tier.");
        }

        long previous = 0;
        for (var index = 0; index < tiers.Count; index++)
        {
            var tier = tiers[index];
            var last = index == tiers.Count - 1;
            if (last != (tier.UpTo is null))
            {
                return PriceErrors.Invalid("tiers", "Only the last tier is unbounded (upTo null), and it always is.");
            }

            if (tier.UpTo is { } upTo)
            {
                if (upTo <= previous)
                {
                    return PriceErrors.Invalid("tiers", "Tiers ascend: each upTo is greater than the one before, and positive.");
                }

                previous = upTo;
            }

            if ((tier.UnitAmount is null) == (tier.UnitAmountDecimal is null))
            {
                return PriceErrors.Invalid("tiers", "Each tier has exactly one of unitAmount and unitAmountDecimal.");
            }

            if (ValidateAmounts("tiers", tier.UnitAmount, tier.UnitAmountDecimal, tier.FlatAmount) is { } invalid)
            {
                return invalid;
            }
        }

        return null;
    }

    private static Error? ValidateAmounts(string field, long? amount, decimal? fractional, long? flatAmount)
    {
        if (amount < 0 || flatAmount < 0 || fractional < 0)
        {
            return PriceErrors.Invalid(field, "Amounts are not negative.");
        }

        if (fractional is { } value && (decimal.Round(value, UnitAmountDecimalScale) != value || value >= 10_000_000_000_000_000m))
        {
            return PriceErrors.Invalid(field, "Fractional unit amounts have at most 12 decimals and 16 integer digits.");
        }

        return null;
    }

    private static int MaxCount(PriceInterval interval) => interval switch
    {
        PriceInterval.Day => 365,
        PriceInterval.Week => 52,
        PriceInterval.Month => 12,
        _ => 1,
    };
}
