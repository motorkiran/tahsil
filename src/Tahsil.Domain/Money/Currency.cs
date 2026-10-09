using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Tahsil.Domain.Money;

/// <summary>
/// An active ISO 4217 currency and its exponent, the number of digits of its minor unit (2 for TRY: kuruş; 0 for
/// JPY; 3 for KWD). Fund codes (such as BOV or CLF) and precious metals are not currencies Tahsil charges in.
/// </summary>
public sealed record Currency(string Code, int Exponent)
{
    private static readonly FrozenDictionary<string, Currency> All = BuildTable();

    public static IReadOnlyCollection<Currency> Supported => All.Values;

    public static bool TryGet(string? code, [NotNullWhen(true)] out Currency? currency)
    {
        currency = null;
        return code is { Length: 3 } && All.TryGetValue(code, out currency);
    }

    /// <exception cref="ArgumentException">The code is not a supported currency.</exception>
    public static Currency Get(string code) =>
        TryGet(code, out var currency) ? currency : throw new ArgumentException($"'{code}' is not a supported currency.", nameof(code));

    private static FrozenDictionary<string, Currency> BuildTable()
    {
        const string TwoDigits =
            "AED AFN ALL AMD AOA ARS AUD AWG AZN BAM BBD BDT BMD BND BOB BRL BSD BTN BWP BYN BZD CAD CDF CHF CNY COP CRC CUP CVE "
            + "CZK DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GTQ GYD HKD HNL HTG HUF IDR ILS INR IRR JMD KES KGS "
            + "KHR KPW KYD KZT LAK LBP LKR LRD LSL MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MYR MZN NAD NGN NIO NOK NPR "
            + "NZD PAB PEN PGK PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS "
            + "TMT TOP TRY TTD TWD TZS UAH USD UYU UZS VED VES WST XCD XCG YER ZAR ZMW ZWG";
        const string NoDigits = "BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX VND VUV XAF XOF XPF";
        const string ThreeDigits = "BHD IQD JOD KWD LYD OMR TND";

        return new[] { (TwoDigits, 2), (NoDigits, 0), (ThreeDigits, 3) }
            .SelectMany(group => group.Item1.Split(' ').Select(code => new Currency(code, group.Item2)))
            .ToFrozenDictionary(currency => currency.Code, StringComparer.Ordinal);
    }
}
