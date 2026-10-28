using System.Globalization;

namespace Tahsil.Contracts;

/// <summary>
/// Amounts are <see langword="long"/> minor units of an ISO 4217 currency, such as kuruş (SDK-3); this formats them
/// for display without floating point.
/// </summary>
public static class TahsilMoney
{
    private static readonly decimal[] Powers = [1m, 10m, 100m, 1000m];

    private static readonly HashSet<string> NoDecimals = new(StringComparer.Ordinal)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    private static readonly HashSet<string> ThreeDecimals = new(StringComparer.Ordinal) { "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND" };

    /// <summary>The decimals of a currency: 2 for most, such as TRY, USD and EUR.</summary>
    public static int Exponent(string currency) => NoDecimals.Contains(currency) ? 0 : ThreeDecimals.Contains(currency) ? 3 : 2;

    /// <summary>The amount in the currency's unit: 12345 kuruş is 123.45.</summary>
    public static decimal ToMajorUnits(long amount, string currency) => amount / Powers[Exponent(currency)];

    /// <summary>An amount for display, symbol first, in the culture's digits: <c>₺1.234,50</c> in Turkish, <c>₺1,234.50</c> in English.</summary>
    public static string Format(long amount, string currency, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var format = (NumberFormatInfo)(culture ?? CultureInfo.CurrentCulture).NumberFormat.Clone();
        format.CurrencySymbol = currency switch
        {
            "TRY" => "₺",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _ => currency + " ",
        };
        format.CurrencyDecimalDigits = Exponent(currency);
        format.CurrencyPositivePattern = 0;
        format.CurrencyNegativePattern = 1;
        return ToMajorUnits(amount, currency).ToString("C", format);
    }
}
