using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tahsil.Contracts;

public enum CouponDuration
{
    Once,
    Repeating,
    Forever,
}

public sealed record CouponSummary(Guid Id, string Key, string Name);

public sealed record CouponResponse(
    Guid Id,
    string Key,
    string Name,
    decimal? PercentOff,
    long? AmountOff,
    string? Currency,
    CouponDuration Duration,
    int? DurationInCycles,
    IReadOnlyList<ProductSummary>? Products,
    int? MaxRedemptions,
    DateTimeOffset? RedeemBy,
    int TimesRedeemed,
    bool IsArchived,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateCouponRequest
{
    [Required]
    public required string Key { get; init; }

    /// <summary>Shown to customers, such as <c>Launch discount</c>.</summary>
    [Required]
    public required string Name { get; init; }

    /// <summary>Above 0 and at most 100, with up to two decimals; or give <see cref="AmountOff"/>.</summary>
    public decimal? PercentOff { get; init; }

    /// <summary>In minor units of <see cref="Currency"/>; or give <see cref="PercentOff"/>.</summary>
    public long? AmountOff { get; init; }

    public string? Currency { get; init; }

    public required CouponDuration Duration { get; init; }

    /// <summary>For <c>repeating</c>: the number of cycle invoices, 1 to 120.</summary>
    public int? DurationInCycles { get; init; }

    /// <summary>IDs or keys of the products it applies to; every product when left out.</summary>
    public IReadOnlyList<string>? Products { get; init; }

    public int? MaxRedemptions { get; init; }

    /// <summary>No redemptions after this time.</summary>
    public DateTimeOffset? RedeemBy { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>JSON Merge Patch: omitted fields stay, <c>null</c> clears. The discount, duration and products never change.</summary>
public sealed record UpdateCouponRequest
{
    public string? Name { get; init; }

    /// <summary>Not below the redemptions so far; <c>null</c> removes the limit.</summary>
    public int? MaxRedemptions
    {
        get;
        init
        {
            field = value;
            HasMaxRedemptions = true;
        }
    }

    public DateTimeOffset? RedeemBy
    {
        get;
        init
        {
            field = value;
            HasRedeemBy = true;
        }
    }

    /// <summary>Archived coupons and their codes redeem no more; existing discounts continue.</summary>
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

    /// <summary>Any other member, such as an attempt to change the discount.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; init; }

    [JsonIgnore]
    public bool HasMaxRedemptions { get; private init; }

    [JsonIgnore]
    public bool HasRedeemBy { get; private init; }

    [JsonIgnore]
    public bool HasMetadata { get; private init; }
}

public sealed record PromotionCodeResponse(
    Guid Id,
    string Code,
    CouponSummary Coupon,
    bool IsActive,
    DateTimeOffset? ExpiresAt,
    int? MaxRedemptions,
    int TimesRedeemed,
    bool FirstTimeOnly,
    long? MinimumAmount,
    string? MinimumAmountCurrency,
    Guid? CustomerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreatePromotionCodeRequest
{
    /// <summary>The coupon's ID or key.</summary>
    [Required]
    public required string Coupon { get; init; }

    /// <summary>3 to 40 letters, digits, <c>_</c> or <c>-</c>; stored in uppercase and matched ignoring case.</summary>
    [Required]
    public required string Code { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public int? MaxRedemptions { get; init; }

    /// <summary>Only customers without a paid invoice may redeem it.</summary>
    public bool FirstTimeOnly { get; init; }

    /// <summary>The smallest invoice subtotal, in minor units of <see cref="MinimumAmountCurrency"/>.</summary>
    public long? MinimumAmount { get; init; }

    public string? MinimumAmountCurrency { get; init; }

    /// <summary>The only customer who may redeem it.</summary>
    public Guid? Customer { get; init; }
}

/// <summary>Only the active flag changes: deactivate a code to stop it, reactivate it to allow it again.</summary>
public sealed record UpdatePromotionCodeRequest
{
    public bool? IsActive { get; init; }

    /// <summary>Any other member, such as an attempt to change a restriction.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; init; }
}
