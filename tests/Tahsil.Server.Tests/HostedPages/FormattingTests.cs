using Tahsil.Application.Common;

namespace Tahsil.Server.Tests.HostedPages;

[Trait("Requirement", "FND-43")]
public sealed class FormattingTests
{
    [Theory]
    [InlineData(123456, "TRY", 2, "tr", "₺1.234,56")]
    [InlineData(123456, "TRY", 2, "en", "₺1,234.56")]
    [InlineData(-29900, "TRY", 2, "tr", "-₺299,00")]
    [InlineData(123456, "EUR", 2, "tr", "€1.234,56")]
    [InlineData(123456, "USD", 2, "en", "$1,234.56")]
    [InlineData(1234567, "KWD", 3, "en", "1,234.567")]
    public void Money_UsesTheReadersCulture_AndTheCurrencysDigits(long minorUnits, string currency, int exponent, string locale, string expected)
    {
        var formatted = Formatting.Money(minorUnits, currency, exponent, Formatting.CultureFor(locale));

        if (currency == "KWD")
        {
            // The symbol of the dinar varies between ICU versions; the digits do not.
            formatted.ShouldEndWith(expected);
        }
        else
        {
            formatted.ShouldBe(expected);
        }
    }

    [Theory]
    [InlineData("tr", "10 Ekim 2026")]
    [InlineData("en", "October 10, 2026")]
    public void Dates_AreShownInTheGivenTimeZone(string locale, string expected)
    {
        var lateEvening = new DateTimeOffset(2026, 10, 9, 21, 30, 0, TimeSpan.Zero);
        var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

        Formatting.Date(lateEvening, istanbul, Formatting.CultureFor(locale)).ShouldBe(expected);
    }
}
