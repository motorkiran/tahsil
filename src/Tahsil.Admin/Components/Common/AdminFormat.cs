using System.Globalization;
using Tahsil.Domain.Money;

namespace Tahsil.Admin.Components.Common;

/// <summary>Amounts as administrators read and type them: in the currency's major unit, such as <c>299.00 TRY</c>.</summary>
public static class AdminFormat
{
    public static string Money(long? minorUnits, string currency)
    {
        if (minorUnits is not { } amount)
        {
            return "–";
        }

        var exponent = Currency.TryGet(currency, out var known) ? known.Exponent : 2;
        var major = amount / (decimal)Math.Pow(10, exponent);
        return $"{major.ToString($"N{exponent}", CultureInfo.InvariantCulture)} {currency}";
    }

    /// <summary>
    /// A typed amount in minor units, or <see langword="null"/> when it is not a number or has more decimals than the
    /// currency has: <c>299.9</c> TRY is 29990.
    /// </summary>
    public static long? ToMinorUnits(decimal? major, string currency)
    {
        if (major is not { } amount || !Currency.TryGet(currency, out var known))
        {
            return null;
        }

        var scaled = amount * (decimal)Math.Pow(10, known.Exponent);
        return decimal.Truncate(scaled) == scaled ? (long)scaled : null;
    }
}
