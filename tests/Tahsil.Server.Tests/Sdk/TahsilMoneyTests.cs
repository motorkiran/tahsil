using System.Globalization;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Sdk;

[Trait("Requirement", "SDK-3")]
public sealed class TahsilMoneyTests
{
    [Theory]
    [InlineData(123450L, "TRY", "tr-TR", "₺1.234,50")]
    [InlineData(123450L, "TRY", "en-US", "₺1,234.50")]
    [InlineData(-500L, "EUR", "en-US", "-€5.00")]
    [InlineData(1500L, "JPY", "en-US", "JPY 1,500")]
    [InlineData(1234L, "KWD", "en-US", "KWD 1.234")]
    public void Amounts_AreFormattedFromMinorUnits(long amount, string currency, string culture, string expected) =>
        TahsilMoney.Format(amount, currency, CultureInfo.GetCultureInfo(culture)).ShouldBe(expected);
}
