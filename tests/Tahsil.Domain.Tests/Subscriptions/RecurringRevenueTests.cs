using Tahsil.Domain.Catalog;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Domain.Tests.Subscriptions;

/// <summary>The vectors of ADM-5: monthly recurring revenue with mixed intervals, quantities, discounts, taxes and currencies.</summary>
[Trait("Requirement", "ADM-5")]
public sealed class RecurringRevenueTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);
    private static readonly Product Pro = Product.Create("pro", "Pro", null, null, null, Now).Value;
    private static readonly Product Support = Product.Create("support", "Support", null, null, null, Now).Value;
    private static readonly TaxRateSnapshot Included = new(20, Inclusive: true);
    private static readonly TaxRateSnapshot Added = new(20, Inclusive: false);

    [Theory]
    [InlineData(PriceInterval.Month, 1, 30_000, 2, "60000")]
    [InlineData(PriceInterval.Year, 1, 300_000, 1, "25000")]
    [InlineData(PriceInterval.Month, 3, 90_000, 1, "30000")]
    [InlineData(PriceInterval.Week, 1, 6_000, 1, "26000")]
    [InlineData(PriceInterval.Week, 2, 6_000, 1, "13000")]
    [InlineData(PriceInterval.Day, 1, 1_200, 1, "36500")]
    public void EveryInterval_IsBroughtToAMonth(PriceInterval interval, int count, long unitAmount, int quantity, string expected) =>
        RecurringRevenue.Monthly("TRY", [new RecurringItem(Price(unitAmount, interval, count), quantity, null)], null).ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void TaxThatPricesInclude_IsLeftOut_AndTaxThatIsAddedNeverCounted()
    {
        RecurringRevenue.Monthly("TRY", [new RecurringItem(Price(30_000), 1, Included)], null).ShouldBe(25_000);
        RecurringRevenue.Monthly("TRY", [new RecurringItem(Price(30_000), 1, Added)], null).ShouldBe(30_000);
    }

    [Fact]
    public void ADiscount_ComesOffBeforeTax_OnTheProductsItAppliesTo()
    {
        RecurringItem[] items = [new(Price(30_000), 2, null), new(Price(10_000, product: Support), 1, null)];

        RecurringRevenue.Monthly("TRY", items, new RecurringDiscount(25, null, null, null)).ShouldBe(52_500);
        RecurringRevenue.Monthly("TRY", items, new RecurringDiscount(25, null, null, [Support.Id])).ShouldBe(67_500);
        RecurringRevenue.Monthly("TRY", items, new RecurringDiscount(null, 14_000, "TRY", null)).ShouldBe(56_000);
        RecurringRevenue.Monthly("TRY", items, new RecurringDiscount(null, 14_000, "TRY", [Support.Id])).ShouldBe(60_000, "an amount never takes more than the lines it applies to");
        RecurringRevenue.Monthly("TRY", items, new RecurringDiscount(null, 14_000, "USD", null)).ShouldBe(70_000, "an amount in another currency does not apply");
        RecurringRevenue.Monthly("TRY", [new RecurringItem(Price(300_000, PriceInterval.Year), 1, Included)], new RecurringDiscount(50, null, null, null))
            .ShouldBe(125_000m / 12);
    }

    [Fact]
    public void MeteredItems_AreLeftOut()
    {
        var metered = Domain.Catalog.Price.Create(
            new PriceDraft { Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Metered, UnitAmount = 10 },
            Pro,
            null,
            Meter.Create("api_calls", "API calls", MeterAggregation.Sum, null, Now).Value,
            Now).Value;

        RecurringRevenue.Monthly("TRY", [new RecurringItem(Price(30_000), 1, null), new RecurringItem(metered, 1, null)], null).ShouldBe(30_000);
    }

    private static Domain.Catalog.Price Price(long unitAmount, PriceInterval interval = PriceInterval.Month, int count = 1, Product? product = null) =>
        Domain.Catalog.Price.Create(
            new PriceDraft { Currency = "TRY", Type = PriceType.Recurring, Interval = interval, IntervalCount = count, UsageType = UsageType.Licensed, UnitAmount = unitAmount },
            product ?? Pro,
            null,
            null,
            Now).Value;
}
