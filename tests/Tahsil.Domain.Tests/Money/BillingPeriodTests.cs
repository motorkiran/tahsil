using System.Globalization;
using CsCheck;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Tests.Money;

/// <summary>Billing periods and proration (MNY §8, §9): every vector and the properties of MNY §10.</summary>
public sealed class BillingPeriodTests
{
    [Theory]
    [Trait("Requirement", "MNY-26")]
    [Trait("Requirement", "MNY-27")]
    [InlineData("UTC", "2026-01-31T10:00Z", PriceInterval.Month, 1, 1, "2026-02-28T10:00Z")]
    [InlineData("UTC", "2026-01-31T10:00Z", PriceInterval.Month, 1, 2, "2026-03-31T10:00Z")]
    [InlineData("UTC", "2026-01-31T10:00Z", PriceInterval.Month, 1, 3, "2026-04-30T10:00Z")]
    [InlineData("UTC", "2026-01-31T10:00Z", PriceInterval.Month, 1, 13, "2027-02-28T10:00Z")]
    [InlineData("UTC", "2028-02-29T00:00Z", PriceInterval.Year, 1, 1, "2029-02-28T00:00Z")]
    [InlineData("UTC", "2028-02-29T00:00Z", PriceInterval.Year, 1, 4, "2032-02-29T00:00Z")]
    [InlineData("UTC", "2026-03-30T08:00Z", PriceInterval.Month, 3, 3, "2026-12-30T08:00Z")]
    [InlineData("UTC", "2026-10-09T12:00Z", PriceInterval.Week, 2, 1, "2026-10-23T12:00Z")]
    [InlineData("UTC", "2026-01-30T22:00Z", PriceInterval.Month, 1, 1, "2026-02-28T22:00Z")]
    [InlineData("Europe/Istanbul", "2026-01-30T22:00Z", PriceInterval.Month, 1, 1, "2026-02-27T22:00Z")]
    [InlineData("Europe/Berlin", "2026-01-29T01:30Z", PriceInterval.Month, 1, 1, "2026-02-28T01:30Z")]
    [InlineData("Europe/Berlin", "2026-01-29T01:30Z", PriceInterval.Month, 1, 2, "2026-03-29T01:30Z")]
    [InlineData("Europe/Berlin", "2026-09-25T00:30Z", PriceInterval.Month, 1, 1, "2026-10-25T00:30Z")]
    public void PeriodStarts_MatchTheVectors(string zone, string anchor, PriceInterval interval, int count, int n, string start) =>
        BillingPeriods.Start(Instant(anchor), interval, count, n, TimeZoneInfo.FindSystemTimeZoneById(zone)).ShouldBe(Instant(start));

    [Theory]
    [Trait("Requirement", "MNY-29")]
    [Trait("Requirement", "MNY-30")]
    [InlineData("2026-04-16T00:00Z", 30_000, 60_000, -15_000, 30_000)]
    [InlineData("2026-04-11T00:00Z", 29_900, 49_900, -19_933, 33_267)]
    [InlineData("2026-04-16T00:00Z", 90_000, 150_000, -45_000, 75_000)]
    [InlineData("2026-05-01T00:00Z", 30_000, 60_000, 0, 0)]
    [InlineData("2026-03-20T00:00Z", 30_000, 60_000, -30_000, 60_000)]
    public void Proration_MatchesTheVectors(string at, long oldAmount, long newAmount, long credit, long charge)
    {
        var period = new BillingPeriod(Instant("2026-04-01T00:00Z"), Instant("2026-05-01T00:00Z"));

        Proration.Amounts(oldAmount, newAmount, Proration.Fraction(period, Instant(at))).ShouldBe(new ProrationAmounts(credit, charge));
    }

    [Fact]
    [Trait("Requirement", "MNY-30")]
    public void ProrationLines_TakeTaxLikeAnyLine()
    {
        var amounts = Proration.Amounts(90_000, 150_000, 0.5m);
        var rate = new TaxRateSnapshot(20, Inclusive: true);

        (LineArithmetic.Tax(amounts.Credit, 0, rate, exempt: false).Tax, LineArithmetic.Tax(amounts.Charge, 0, rate, exempt: false).Tax).ShouldBe((-7_500L, 12_500L));
    }

    [Fact]
    [Trait("Requirement", "MNY-26")]
    [Trait("Requirement", "MNY-27")]
    public void Periods_GrowWithoutDrift_AndKeepTheAnchorsDay() =>
        Gen.Select(Gen.DateTime[new DateTime(2020, 1, 1), new DateTime(2035, 12, 31)], Gen.Enum<PriceInterval>(), Gen.Int[1, 12], Gen.Int[0, 60], Gen.OneOfConst("UTC", "Europe/Istanbul", "Europe/Berlin", "America/New_York"))
            .Sample((date, interval, count, n, zoneId) =>
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                var anchor = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
                var start = BillingPeriods.Start(anchor, interval, count, n, zone);
                var next = BillingPeriods.Start(anchor, interval, count, n + 1, zone);
                start.ShouldBeLessThan(next);
                if (interval is PriceInterval.Month or PriceInterval.Year)
                {
                    var anchorDay = TimeZoneInfo.ConvertTime(anchor, zone).Day;
                    var local = TimeZoneInfo.ConvertTime(start, zone);
                    (local.Day == anchorDay || local.Day == DateTime.DaysInMonth(local.Year, local.Month) || IsShiftedByGap(anchor, start, zone)).ShouldBeTrue();
                }

                var (index, period) = BillingPeriods.PeriodAt(anchor, interval, count, start, zone);
                (index, period.Start).ShouldBe((n, start));
            });

    [Fact]
    [Trait("Requirement", "MNY-29")]
    [Trait("Requirement", "MNY-30")]
    public void Proration_CreditsFullyAtTheStart_NothingAtTheEnd_AndNetsTheRoundedShares() =>
        Gen.Select(Gen.Long[0, 10_000_000_000L], Gen.Long[0, 10_000_000_000L], Gen.Int[1, 400 * 86_400], Gen.Int[0, 400 * 86_400]).Sample((oldAmount, newAmount, length, offset) =>
        {
            var start = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
            var period = new BillingPeriod(start, start.AddSeconds(length));
            var at = start.AddSeconds(Math.Min(offset, length));
            var fraction = Proration.Fraction(period, at);

            Proration.Amounts(oldAmount, newAmount, Proration.Fraction(period, period.Start)).Credit.ShouldBe(-oldAmount);
            Proration.Amounts(oldAmount, newAmount, Proration.Fraction(period, period.End)).ShouldBe(new ProrationAmounts(0, 0));
            Proration.Amounts(oldAmount, newAmount, fraction).Net.ShouldBe(MoneyMath.Round(newAmount * fraction) - MoneyMath.Round(oldAmount * fraction));
        });

    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>A start whose local time fell into a daylight saving gap moved forward, which may cross midnight in some zones.</summary>
    private static bool IsShiftedByGap(DateTimeOffset anchor, DateTimeOffset start, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(start, zone).TimeOfDay != TimeZoneInfo.ConvertTime(anchor, zone).TimeOfDay;
}
