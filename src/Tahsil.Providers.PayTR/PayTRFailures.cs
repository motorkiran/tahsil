using System.Globalization;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.PayTR;

/// <summary>
/// PayTR's failure reasons as Tahsil's failure codes (§6): the reason code, and for code 0 the bank's message by
/// keyword, which stays provisional until PayTR publishes the banks' message catalogs.
/// </summary>
internal static class PayTRFailures
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static FailureCode Map(string? reasonCode, string? message) => reasonCode switch
    {
        "1" or "2" => FailureCode.AuthenticationFailed,
        "3" or "11" => FailureCode.FraudSuspected,
        "6" => FailureCode.Abandoned,
        "8" or "99" => FailureCode.InvalidRequest,
        "9" => FailureCode.CardDeclined,
        "10" => FailureCode.AuthenticationRequired,
        _ => ByMessage(message),
    };

    /// <summary>A bank's message, lowercased the Turkish way, by keyword.</summary>
    public static FailureCode ByMessage(string? message)
    {
        var text = message?.ToLower(Turkish) ?? string.Empty;
        bool Has(params string[] words) => words.Any(word => text.Contains(word, StringComparison.Ordinal));
        return true switch
        {
            _ when Has("bakiye") || (text.Contains("limit", StringComparison.Ordinal) && text.Contains("yetersiz", StringComparison.Ordinal)) => FailureCode.InsufficientFunds,
            _ when Has("limit aş", "günlük") => FailureCode.LimitExceeded,
            _ when Has("son kullanma", "süresi dol") => FailureCode.ExpiredCard,
            _ when Has("kayıp", "çalıntı") => FailureCode.LostOrStolen,
            _ when Has("geçersiz kart", "kart numarası", "cvv", "güvenlik kodu") => FailureCode.InvalidCard,
            _ when Has("şüpheli", "fraud") => FailureCode.FraudSuspected,
            _ when Has("sistem", "zaman aşımı", "ulaşılam") => FailureCode.ProviderUnavailable,
            _ => FailureCode.CardDeclined,
        };
    }
}
