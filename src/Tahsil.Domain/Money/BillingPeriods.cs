using Tahsil.Domain.Catalog;

namespace Tahsil.Domain.Money;

/// <summary>A billing period, [Start, End).</summary>
public readonly record struct BillingPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;

    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;
}

/// <summary>
/// Billing periods (MNY-26, MNY-27): computed from the anchor in the billing time zone, never from the previous period,
/// so they do not drift; months clamp to the target month's last day, and the local time of day stays the anchor's.
/// </summary>
public static class BillingPeriods
{
    /// <summary>
    /// The start of period <paramref name="n"/>; a negative <paramref name="n"/> counts back from the anchor, such as the
    /// full period a shorter first period is a share of (SUB-4).
    /// </summary>
    public static DateTimeOffset Start(DateTimeOffset anchor, PriceInterval interval, int intervalCount, long n, TimeZoneInfo zone)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalCount);
        var local = TimeZoneInfo.ConvertTime(anchor, zone).DateTime;
        var units = checked((int)(n * intervalCount));
        var shifted = interval switch
        {
            PriceInterval.Day => local.AddDays(units),
            PriceInterval.Week => local.AddDays(checked(units * 7)),
            PriceInterval.Month => local.AddMonths(units),
            PriceInterval.Year => local.AddMonths(checked(units * 12)),
            _ => throw new ArgumentOutOfRangeException(nameof(interval)),
        };

        return ToInstant(DateTime.SpecifyKind(shifted, DateTimeKind.Unspecified), zone);
    }

    /// <summary>Period <paramref name="n"/>: [start(n), start(n+1)).</summary>
    public static BillingPeriod Period(DateTimeOffset anchor, PriceInterval interval, int intervalCount, long n, TimeZoneInfo zone) =>
        new(Start(anchor, interval, intervalCount, n, zone), Start(anchor, interval, intervalCount, n + 1, zone));

    /// <summary>The period that contains <paramref name="instant"/>, which is at or after the anchor.</summary>
    public static (long Index, BillingPeriod Period) PeriodAt(DateTimeOffset anchor, PriceInterval interval, int intervalCount, DateTimeOffset instant, TimeZoneInfo zone)
    {
        if (instant < anchor)
        {
            throw new ArgumentOutOfRangeException(nameof(instant), instant, "Periods start at the anchor.");
        }

        // An estimate from the average length, then a few steps to the exact period.
        var average = interval switch
        {
            PriceInterval.Day => TimeSpan.FromDays(1),
            PriceInterval.Week => TimeSpan.FromDays(7),
            PriceInterval.Month => TimeSpan.FromDays(30.436875),
            _ => TimeSpan.FromDays(365.2425),
        } * intervalCount;
        var n = Math.Max(0, (long)((instant - anchor) / average) - 1);
        while (Start(anchor, interval, intervalCount, n, zone) > instant)
        {
            n--;
        }

        while (Start(anchor, interval, intervalCount, n + 1, zone) <= instant)
        {
            n++;
        }

        return (n, Period(anchor, interval, intervalCount, n, zone));
    }

    /// <summary>
    /// A local time as an instant (MNY-27 step 3): a time in a daylight saving gap moves forward by the gap's length, and
    /// a repeated time takes its first occurrence.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            local += zone.GetUtcOffset(local.AddHours(6)) - zone.GetUtcOffset(local.AddHours(-6));
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
