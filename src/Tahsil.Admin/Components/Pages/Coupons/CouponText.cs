using System.Globalization;
using Tahsil.Admin.Components.Common;
using Tahsil.Contracts;

namespace Tahsil.Admin.Components.Pages.Coupons;

/// <summary>Coupons and codes in the panel's words.</summary>
internal static class CouponText
{
    /// <summary>Such as <c>50% off for 3 cycles</c> or <c>100.00 TRY off once</c>.</summary>
    public static string Discount(CouponResponse coupon)
    {
        var off = coupon.PercentOff is { } percent
            ? $"{percent.ToString("0.##", CultureInfo.InvariantCulture)}%"
            : AdminFormat.Money(coupon.AmountOff, coupon.Currency!);
        var duration = coupon.Duration switch
        {
            CouponDuration.Once => "once",
            CouponDuration.Repeating => coupon.DurationInCycles == 1 ? "for 1 cycle" : $"for {coupon.DurationInCycles} cycles",
            _ => "forever",
        };
        return $"{off} off {duration}";
    }

    /// <summary>Such as <c>12 of 500</c>, or <c>12</c> without a limit.</summary>
    public static string Redemptions(int times, int? max) => max is { } limit ? $"{times} of {limit}" : times.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a code asks of who redeems it, such as <c>first purchase only, at least 500.00 TRY</c>.</summary>
    public static string Restrictions(PromotionCodeResponse code)
    {
        var restrictions = new List<string>();
        if (code.ExpiresAt is { } expires)
        {
            restrictions.Add($"until {expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
        }

        if (code.FirstTimeOnly)
        {
            restrictions.Add("first purchase only");
        }

        if (code.MinimumAmount is { } minimum)
        {
            restrictions.Add($"at least {AdminFormat.Money(minimum, code.MinimumAmountCurrency!)}");
        }

        if (code.CustomerId is not null)
        {
            restrictions.Add("one customer");
        }

        return restrictions.Count == 0 ? "–" : string.Join(", ", restrictions);
    }
}
