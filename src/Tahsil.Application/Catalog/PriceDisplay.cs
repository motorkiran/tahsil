using Tahsil.Contracts;

namespace Tahsil.Application.Catalog;

/// <summary>How prices read on pages, emails and invoices (CAT-13, CAT-14).</summary>
public static class PriceDisplay
{
    /// <summary>The product name, then the price name in parentheses when there is one: <c>Pro (Monthly)</c>.</summary>
    public static string Name(string productName, string? priceName) =>
        string.IsNullOrWhiteSpace(priceName) ? productName : $"{productName} ({priceName})";

    /// <summary>The recurrence in <c>tr</c> or <c>en</c>: <c>monthly</c>, <c>3 ayda bir</c>, <c>every 2 weeks</c>.</summary>
    public static string Recurrence(PriceInterval interval, int count, string locale)
    {
        var turkish = locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        return (interval, count, turkish) switch
        {
            (PriceInterval.Day, 1, true) => "günlük",
            (PriceInterval.Day, 1, false) => "daily",
            (PriceInterval.Day, _, true) => $"{count} günde bir",
            (PriceInterval.Day, _, false) => $"every {count} days",
            (PriceInterval.Week, 1, true) => "haftalık",
            (PriceInterval.Week, 1, false) => "weekly",
            (PriceInterval.Week, _, true) => $"{count} haftada bir",
            (PriceInterval.Week, _, false) => $"every {count} weeks",
            (PriceInterval.Month, 1, true) => "aylık",
            (PriceInterval.Month, 1, false) => "monthly",
            (PriceInterval.Month, 3, false) => "quarterly",
            (PriceInterval.Month, _, true) => $"{count} ayda bir",
            (PriceInterval.Month, _, false) => $"every {count} months",
            (_, _, true) => "yıllık",
            _ => "yearly",
        };
    }
}
