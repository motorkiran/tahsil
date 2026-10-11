namespace Tahsil.Domain.Money;

/// <summary>A change's two proration amounts (MNY-30): the credit for what was, the charge for what becomes.</summary>
public readonly record struct ProrationAmounts(long Credit, long Charge)
{
    public long Net => checked(Credit + Charge);
}

/// <summary>Proration of a change within a period (MNY-29 to MNY-32).</summary>
public static class Proration
{
    /// <summary>The remaining fraction of [start, end) at <paramref name="at"/>, from whole seconds, with the instant clamped (MNY-29).</summary>
    public static decimal Fraction(BillingPeriod period, DateTimeOffset at)
    {
        var clamped = at < period.Start ? period.Start : at > period.End ? period.End : at;
        var total = period.Length.Ticks / TimeSpan.TicksPerSecond;
        if (total <= 0)
        {
            return 0;
        }

        return (period.End - clamped).Ticks / TimeSpan.TicksPerSecond / (decimal)total;
    }

    /// <summary>
    /// The credit −round(old × f) and the charge round(new × f) of an item, from full-period amounts before discounts
    /// (MNY-30). An amount of 0 means the line is omitted.
    /// </summary>
    public static ProrationAmounts Amounts(long oldAmount, long newAmount, decimal fraction) =>
        new(-MoneyMath.Round(oldAmount * fraction), MoneyMath.Round(newAmount * fraction));
}
