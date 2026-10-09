using Tahsil.Application.Catalog;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Catalog;

public sealed class PriceDisplayTests
{
    [Theory]
    [Trait("Requirement", "CAT-13")]
    [InlineData("Pro", "Monthly", "Pro (Monthly)")]
    [InlineData("Pro", null, "Pro")]
    [InlineData("Pro", " ", "Pro")]
    public void DisplayNames_JoinTheProductAndPriceNames(string product, string? price, string expected)
    {
        PriceDisplay.Name(product, price).ShouldBe(expected);
    }

    [Theory]
    [Trait("Requirement", "CAT-14")]
    [InlineData(PriceInterval.Day, 1, "daily", "günlük")]
    [InlineData(PriceInterval.Day, 10, "every 10 days", "10 günde bir")]
    [InlineData(PriceInterval.Week, 1, "weekly", "haftalık")]
    [InlineData(PriceInterval.Week, 2, "every 2 weeks", "2 haftada bir")]
    [InlineData(PriceInterval.Month, 1, "monthly", "aylık")]
    [InlineData(PriceInterval.Month, 3, "quarterly", "3 ayda bir")]
    [InlineData(PriceInterval.Month, 6, "every 6 months", "6 ayda bir")]
    [InlineData(PriceInterval.Year, 1, "yearly", "yıllık")]
    public void Recurrences_ReadNaturally_InBothLanguages(PriceInterval interval, int count, string english, string turkish)
    {
        PriceDisplay.Recurrence(interval, count, "en").ShouldBe(english);
        PriceDisplay.Recurrence(interval, count, "tr").ShouldBe(turkish);
    }
}
