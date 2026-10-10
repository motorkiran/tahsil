using System.Globalization;
using System.Numerics;

namespace Tahsil.Providers.Abstractions;

/// <summary>Converts minor units to and from the decimal strings providers use, without floating point (ADP-8).</summary>
public static class AmountFormat
{
    /// <summary>Formats minor units as a decimal with exactly the currency's digits, such as <c>1250</c> → <c>12.50</c>.</summary>
    public static string Fixed(Amount amount) => Fixed(amount.MinorUnits, amount.Exponent);

    public static string Fixed(long minorUnits, int exponent) => ToDecimal(minorUnits, exponent).ToString("F" + exponent.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Formats minor units as the shortest decimal, such as <c>1250</c> → <c>12.5</c> and <c>1200</c> → <c>12</c>.</summary>
    public static string Shortest(Amount amount) =>
        ToDecimal(amount.MinorUnits, amount.Exponent).ToString("0.############################", CultureInfo.InvariantCulture);

    public static decimal ToDecimal(long minorUnits, int exponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exponent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(exponent, 8);
        return new decimal(minorUnits) / (decimal)BigInteger.Pow(10, exponent);
    }

    /// <summary>Reads a decimal amount into minor units; <see langword="null"/> when it has more digits than the currency.</summary>
    public static long? ToMinorUnits(decimal value, int exponent)
    {
        var scaled = value * (decimal)BigInteger.Pow(10, exponent);
        return scaled == decimal.Truncate(scaled) && scaled is >= long.MinValue and <= long.MaxValue ? (long)scaled : null;
    }

    public static long? ToMinorUnits(string? value, int exponent) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? ToMinorUnits(number, exponent)
            : null;
}
