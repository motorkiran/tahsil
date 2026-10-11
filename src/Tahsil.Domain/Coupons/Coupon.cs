using Tahsil.Domain.Common;

namespace Tahsil.Domain.Coupons;

/// <summary>How long a subscription's discount lasts (CPN-12).</summary>
public enum CouponDuration
{
    Once,
    Repeating,
    Forever,
}

/// <summary>
/// A discount, a percentage or an amount in one currency, redeemed directly or through promotion codes (CPN-1). The
/// discount, its duration and its products never change; the name, limits, archive flag and metadata can (CPN-2).
/// Coupons are never deleted (CPN-3).
/// </summary>
public sealed class Coupon : AggregateRoot
{
    public const int NameMaxLength = 100;
    public const int MaxDurationInCycles = 120;

    private Coupon()
    {
    }

    public Guid Id { get; private init; }

    public string Key { get; private init; } = string.Empty;

    /// <summary>Shown to customers on pages, invoices and emails.</summary>
    public string Name { get; private set; } = string.Empty;

    public decimal? PercentOff { get; private init; }

    public long? AmountOff { get; private init; }

    /// <summary>The currency of <see cref="AmountOff"/>.</summary>
    public string? Currency { get; private init; }

    public CouponDuration Duration { get; private init; }

    /// <summary>For <see cref="CouponDuration.Repeating"/>: how many cycle invoices the discount applies to.</summary>
    public int? DurationInCycles { get; private init; }

    /// <summary>The products it applies to; <see langword="null"/> for all.</summary>
    public IReadOnlyList<Guid>? ProductIds { get; private init; }

    public int? MaxRedemptions { get; private set; }

    public DateTimeOffset? RedeemBy { get; private set; }

    public int TimesRedeemed { get; private set; }

    public bool IsArchived { get; private set; }

    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = Common.Metadata.Empty;

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Coupon> Create(CouponDraft draft, DateTimeOffset now)
    {
        if (!Catalog.CatalogKey.IsValid(draft.Key))
        {
            return Catalog.CatalogKey.Invalid;
        }

        if (ValidateDiscount(draft) is { } invalid)
        {
            return invalid;
        }

        var coupon = new Coupon
        {
            Id = Guid.CreateVersion7(now),
            Key = draft.Key,
            PercentOff = draft.PercentOff,
            AmountOff = draft.AmountOff,
            Currency = draft.Currency,
            Duration = draft.Duration,
            DurationInCycles = draft.DurationInCycles,
            ProductIds = draft.ProductIds?.Distinct().ToList(),
            CreatedAt = now,
        };
        var update = coupon.Update(draft.Name, draft.MaxRedemptions, draft.RedeemBy, isArchived: false, draft.Metadata, now);
        return update.IsFailure ? update.Error : coupon;
    }

    public Result Update(string name, int? maxRedemptions, DateTimeOffset? redeemBy, bool isArchived, IReadOnlyDictionary<string, string>? metadata, DateTimeOffset now)
    {
        if (!Text.FitsRequired(name, NameMaxLength))
        {
            return CouponErrors.Invalid("name", "A name is required and is at most 100 characters.");
        }

        if (maxRedemptions < Math.Max(1, TimesRedeemed))
        {
            return CouponErrors.Invalid("maxRedemptions", $"The limit is at least 1 and not below the {TimesRedeemed} redemptions so far.");
        }

        var validMetadata = Common.Metadata.Validate(metadata);
        if (validMetadata.IsFailure)
        {
            return validMetadata.Error;
        }

        Name = name.Trim();
        MaxRedemptions = maxRedemptions;
        RedeemBy = redeemBy;
        IsArchived = isArchived;
        Metadata = validMetadata.Value;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>The rules of CPN-1 for what a coupon takes off and for how long.</summary>
    private static Error? ValidateDiscount(CouponDraft draft)
    {
        if ((draft.PercentOff is null) == (draft.AmountOff is null))
        {
            return CouponErrors.Invalid("percentOff", "A coupon has either percentOff or amountOff with a currency.");
        }

        if (draft.PercentOff is { } percent && (percent <= 0 || percent > 100 || decimal.Round(percent, 2) != percent))
        {
            return CouponErrors.Invalid("percentOff", "percentOff is above 0 and at most 100, with at most two decimals.");
        }

        if (draft.AmountOff is { } amount)
        {
            if (amount <= 0)
            {
                return CouponErrors.Invalid("amountOff", "amountOff is a positive amount in minor units.");
            }

            if (!Money.Currency.TryGet(draft.Currency, out _))
            {
                return CouponErrors.Invalid("currency", "amountOff needs a supported ISO 4217 currency.");
            }
        }
        else if (draft.Currency is not null)
        {
            return CouponErrors.Invalid("currency", "Only an amountOff coupon has a currency.");
        }

        if (!Enum.IsDefined(draft.Duration))
        {
            return CouponErrors.Invalid("duration", "The duration is once, repeating or forever.");
        }

        if (draft.Duration == CouponDuration.Repeating
            ? draft.DurationInCycles is not (>= 1 and <= MaxDurationInCycles)
            : draft.DurationInCycles is not null)
        {
            return CouponErrors.Invalid("durationInCycles", "A repeating coupon lasts 1 to 120 cycles; other coupons have no durationInCycles.");
        }

        return draft.ProductIds is { Count: 0 } ? CouponErrors.Invalid("products", "Leave products out to apply to every product.") : null;
    }
}

/// <summary>What a new coupon is made of.</summary>
public sealed record CouponDraft(
    string Key,
    string Name,
    decimal? PercentOff,
    long? AmountOff,
    string? Currency,
    CouponDuration Duration,
    int? DurationInCycles,
    IReadOnlyList<Guid>? ProductIds,
    int? MaxRedemptions,
    DateTimeOffset? RedeemBy,
    IReadOnlyDictionary<string, string>? Metadata);
