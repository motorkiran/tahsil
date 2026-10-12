using System.Globalization;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Coupons;

namespace Tahsil.Application.Coupons;

/// <summary>How a coupon reads on pages, invoices and emails (CPN §6): its name with its value and, when it lasts a while, for how long.</summary>
public static class CouponDisplay
{
    /// <summary>
    /// Such as <c>Launch discount (−50%, 3 months)</c> or <c>Kampanya indirimi (−₺100,00)</c>. The duration of a
    /// repeating coupon is given in the subscription's interval, when there is one.
    /// </summary>
    public static string Describe(Coupon coupon, string locale, Func<long, string> money, PriceInterval? interval = null, int intervalCount = 1)
    {
        var turkish = locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        var percent = coupon.PercentOff?.ToString("0.##", CultureInfo.GetCultureInfo(turkish ? "tr-TR" : "en-US"));
        var value = coupon.AmountOff is { } amount ? money(amount) : turkish ? $"%{percent}" : $"{percent}%";
        var lasts = coupon is { Duration: CouponDuration.Repeating, DurationInCycles: { } cycles } && interval is { } unit
            ? $", {Span(cycles * intervalCount, unit, turkish)}"
            : null;
        return $"{coupon.Name} (−{value}{lasts})";
    }

    private static string Span(int count, PriceInterval unit, bool turkish) => (unit, turkish, count) switch
    {
        (PriceInterval.Day, true, _) => $"{count} gün",
        (PriceInterval.Day, false, 1) => "1 day",
        (PriceInterval.Day, false, _) => $"{count} days",
        (PriceInterval.Week, true, _) => $"{count} hafta",
        (PriceInterval.Week, false, 1) => "1 week",
        (PriceInterval.Week, false, _) => $"{count} weeks",
        (PriceInterval.Month, true, _) => $"{count} ay",
        (PriceInterval.Month, false, 1) => "1 month",
        (PriceInterval.Month, false, _) => $"{count} months",
        (_, true, _) => $"{count} yıl",
        (_, false, 1) => "1 year",
        _ => $"{count} years",
    };
}
