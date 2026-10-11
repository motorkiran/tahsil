using System.Text.RegularExpressions;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Coupons;

/// <summary>
/// A code customers enter to redeem a coupon, with restrictions of its own (CPN-4, CPN-5). Codes are stored in
/// uppercase and matched ignoring case; only the active flag changes, and codes are never deleted (CPN-6).
/// </summary>
public sealed partial class PromotionCode : AggregateRoot
{
    public const int CodeMaxLength = 40;

    private PromotionCode()
    {
    }

    public Guid Id { get; private init; }

    public Guid CouponId { get; private init; }

    public string Code { get; private init; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private init; }

    public int? MaxRedemptions { get; private init; }

    public int TimesRedeemed { get; private set; }

    /// <summary>Only for customers without a paid invoice.</summary>
    public bool FirstTimeOnly { get; private init; }

    /// <summary>The smallest invoice subtotal the code applies to, in <see cref="MinimumAmountCurrency"/>.</summary>
    public long? MinimumAmount { get; private init; }

    public string? MinimumAmountCurrency { get; private init; }

    /// <summary>The only customer who may redeem the code.</summary>
    public Guid? CustomerId { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A code as stored and compared: trimmed and in uppercase.</summary>
    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    public static Result<PromotionCode> Create(Guid couponId, PromotionCodeDraft draft, DateTimeOffset now)
    {
        var code = Normalize(draft.Code ?? string.Empty);
        if (!CodePattern().IsMatch(code))
        {
            return PromotionCodeErrors.Invalid("code", "A code is 3 to 40 letters, digits, '_' or '-'.");
        }

        if (draft.MaxRedemptions < 1)
        {
            return PromotionCodeErrors.Invalid("maxRedemptions", "The limit is at least 1.");
        }

        if ((draft.MinimumAmount is null) != (draft.MinimumAmountCurrency is null))
        {
            return PromotionCodeErrors.Invalid("minimumAmount", "A minimum amount comes with its currency.");
        }

        if (draft.MinimumAmount <= 0 || (draft.MinimumAmountCurrency is not null && !Money.Currency.TryGet(draft.MinimumAmountCurrency, out _)))
        {
            return PromotionCodeErrors.Invalid("minimumAmount", "The minimum is a positive amount in minor units of a supported currency.");
        }

        return new PromotionCode
        {
            Id = Guid.CreateVersion7(now),
            CouponId = couponId,
            Code = code,
            IsActive = true,
            ExpiresAt = draft.ExpiresAt,
            MaxRedemptions = draft.MaxRedemptions,
            FirstTimeOnly = draft.FirstTimeOnly,
            MinimumAmount = draft.MinimumAmount,
            MinimumAmountCurrency = draft.MinimumAmountCurrency,
            CustomerId = draft.CustomerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void SetActive(bool isActive, DateTimeOffset now)
    {
        IsActive = isActive;
        UpdatedAt = now;
    }

    [GeneratedRegex("^[A-Z0-9_-]{3,40}$")]
    private static partial Regex CodePattern();
}

/// <summary>What a new promotion code is made of.</summary>
public sealed record PromotionCodeDraft(
    string? Code,
    DateTimeOffset? ExpiresAt = null,
    int? MaxRedemptions = null,
    bool FirstTimeOnly = false,
    long? MinimumAmount = null,
    string? MinimumAmountCurrency = null,
    Guid? CustomerId = null);
