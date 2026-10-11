using System.Collections.Concurrent;
using System.Globalization;

namespace Tahsil.Application.Common;

/// <summary>
/// Money and dates as people read them, in the patterns of <c>tr-TR</c> and <c>en-US</c>. Presentation only:
/// formatted values are never parsed back.
/// </summary>
public static class Formatting
{
    private static readonly ConcurrentDictionary<string, string> CurrencySymbols = new(StringComparer.Ordinal);

    /// <summary>The culture whose patterns a locale uses: <c>tr-TR</c> for Turkish, <c>en-US</c> otherwise.</summary>
    public static CultureInfo CultureFor(string? locale) =>
        CultureInfo.GetCultureInfo(locale?.StartsWith("tr", StringComparison.OrdinalIgnoreCase) == true ? "tr-TR" : "en-US");

    /// <summary>An amount in minor units with the currency's symbol: <c>₺1.234,56</c> in Turkish, <c>₺1,234.56</c> in English.</summary>
    /// <param name="minorUnits">The amount in the currency's minor unit (kuruş for TRY).</param>
    /// <param name="currency">The ISO 4217 code.</param>
    /// <param name="exponent">The currency's number of minor-unit digits.</param>
    /// <param name="culture">The reader's culture.</param>
    public static string Money(long minorUnits, string currency, int exponent, CultureInfo culture)
    {
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = Symbol(currency);
        format.CurrencyDecimalDigits = exponent;

        // The same layout in every locale and ICU version: symbol first, minus sign before it.
        format.CurrencyPositivePattern = 0;
        format.CurrencyNegativePattern = 1;

        return (minorUnits / (decimal)Math.Pow(10, exponent)).ToString("C", format);
    }

    /// <summary>A calendar date in a time zone: <c>9 Ekim 2026</c>, <c>October 9, 2026</c>.</summary>
    public static string Date(DateTimeOffset instant, TimeZoneInfo zone, CultureInfo culture) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString(
            culture.TwoLetterISOLanguageName == "tr" ? "d MMMM yyyy" : "MMMM d, yyyy",
            culture);

    /// <summary>The local symbol of a currency (₺, $, €), or its code where no culture has one.</summary>
    private static string Symbol(string currency) => CurrencySymbols.GetOrAdd(currency, static code =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(culture => (Culture: culture, Region: TryGetRegion(culture)))
            .Where(candidate => candidate.Region?.ISOCurrencySymbol == code)
            .Select(candidate => candidate.Region!.CurrencySymbol)
            .OrderBy(symbol => symbol.Length)
            .FirstOrDefault() ?? code + " ");

    private static RegionInfo? TryGetRegion(CultureInfo culture)
    {
        try
        {
            return new RegionInfo(culture.Name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
