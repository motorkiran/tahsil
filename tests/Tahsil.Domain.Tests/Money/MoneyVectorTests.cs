using Tahsil.Domain.Catalog;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Tests.Money;

/// <summary>The vectors of the money specification, one by one.</summary>
public sealed class MoneyVectorTests
{
    private static readonly PriceTier[] T = [new(1000, 10, null, null), new(10000, 8, null, null), new(null, 5, null, null)];

    [Theory]
    [Trait("Requirement", "MNY-5")]
    [InlineData(29900, "0.5", 14950)]
    [InlineData(1000, "0.333333333333333333333333333", 333)]
    [InlineData(1000, "0.666666666666666666666666667", 667)]
    [InlineData(1, "0.5", 1)]
    [InlineData(-1, "0.5", -1)]
    public void Rounding_IsHalfAwayFromZero_Once(long amount, string factor, long expected)
    {
        MoneyMath.Multiply(amount, decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);
    }

    [Theory]
    [Trait("Requirement", "MNY-6")]
    [InlineData(1000, new long[] { 10000, 20000 }, new long[] { 333, 667 })]
    [InlineData(100, new long[] { 1, 1, 1 }, new long[] { 34, 33, 33 })]
    [InlineData(2, new long[] { 1, 1, 1 }, new long[] { 1, 1, 0 })]
    [InlineData(-100, new long[] { 1, 1, 1 }, new long[] { -34, -33, -33 })]
    public void Allocation_UsesTheLargestRemainders(long amount, long[] weights, long[] expected)
    {
        MoneyMath.Allocate(amount, weights).ShouldBe(expected);
    }

    [Fact]
    [Trait("Requirement", "MNY-3")]
    public void MixingCurrencies_OrOverflowing_Throws()
    {
        var lira = new Domain.Money.Money(100, Currency.Get("TRY"));

        Should.Throw<InvalidOperationException>(() => lira + new Domain.Money.Money(100, Currency.Get("USD")));
        Should.Throw<OverflowException>(() => new Domain.Money.Money(long.MaxValue, lira.Currency) + lira);
        (lira + lira - new Domain.Money.Money(50, lira.Currency)).Amount.ShouldBe(150);
    }

    [Theory]
    [Trait("Requirement", "MNY-10")]
    [Trait("Requirement", "MNY-11")]
    [InlineData(TiersMode.Graduated, 12345, 93725)]
    [InlineData(TiersMode.Graduated, 1000, 10000)]
    [InlineData(TiersMode.Volume, 12345, 61725)]
    [InlineData(TiersMode.Volume, 1000, 10000)]
    [InlineData(TiersMode.Volume, 1001, 8008)]
    public void TieredAmounts(TiersMode mode, long quantity, long expected)
    {
        PriceAmounts.For(BillingScheme.Tiered, null, null, null, null, mode, T, quantity).ShouldBe(expected);
    }

    [Theory]
    [Trait("Requirement", "MNY-10")]
    [InlineData(3, 5000)]
    [InlineData(7, 6800)]
    [InlineData(0, 0)]
    public void FlatTiers_CountOnceAnyUnitFallsInThem(long quantity, long expected)
    {
        PriceTier[] tiers = [new(5, 0, null, 5000), new(null, 900, null, null)];

        PriceAmounts.For(BillingScheme.Tiered, null, null, null, null, TiersMode.Graduated, tiers, quantity).ShouldBe(expected);
    }

    [Theory]
    [Trait("Requirement", "MNY-8")]
    [Trait("Requirement", "MNY-9")]
    [InlineData(BillingScheme.PerUnit, null, "0.25", null, null, 12345, 3086)]
    [InlineData(BillingScheme.PerUnit, null, "0.25", null, null, 2, 1)]
    [InlineData(BillingScheme.Package, 500L, null, 1000, PackageRounding.Up, 1, 500)]
    [InlineData(BillingScheme.Package, 500L, null, 1000, PackageRounding.Up, 1001, 1000)]
    [InlineData(BillingScheme.Package, 500L, null, 1000, PackageRounding.Down, 1999, 500)]
    [InlineData(BillingScheme.PerUnit, 29900L, null, null, null, 3, 89700)]
    public void PerUnitAndPackageAmounts(
        BillingScheme scheme, long? unitAmount, string? unitDecimal, int? packageSize, PackageRounding? rounding, long quantity, long expected)
    {
        var fractional = unitDecimal is null ? (decimal?)null : decimal.Parse(unitDecimal, System.Globalization.CultureInfo.InvariantCulture);

        PriceAmounts.For(scheme, unitAmount, fractional, packageSize, rounding, null, null, quantity).ShouldBe(expected);
    }

    [Theory]
    [Trait("Requirement", "MNY-13")]
    [Trait("Requirement", "MNY-14")]
    [Trait("Requirement", "MNY-16")]
    [InlineData(20, false, 10000, 0, 2000, 12000, 10000)]
    [InlineData(18, false, 12345, 0, 2222, 14567, 12345)]
    [InlineData(20, true, 29900, 0, 4983, 29900, 24917)]
    [InlineData(20, true, 100, 0, 17, 100, 83)]
    [InlineData(10, true, 1, 0, 0, 1, 1)]
    [InlineData(1, true, 99900, 0, 989, 99900, 98911)]
    [InlineData(20, true, -29900, 0, -4983, -29900, -24917)]
    [InlineData(20, false, 10000, 2500, 1500, 9000, 7500)]
    public void TaxVectors(int percentage, bool inclusive, long amount, long discount, long tax, long total, long net)
    {
        LineArithmetic.Tax(amount, discount, new TaxRateSnapshot(percentage, inclusive), exempt: false)
            .ShouldBe(new LineTax(amount - discount, tax, total, net));
    }

    [Fact]
    [Trait("Requirement", "MNY-15")]
    public void ExemptCustomers_PayNoTax_AtThePricedAmount()
    {
        LineArithmetic.Tax(29900, 0, new TaxRateSnapshot(20, true), exempt: true).ShouldBe(new LineTax(29900, 0, 29900, 29900));
    }

    [Theory]
    [Trait("Requirement", "MNY-18")]
    [InlineData(29900, 4485)]
    [InlineData(-15000, -2250)]
    public void PercentageDiscounts_KeepTheLinesSign(long amount, long discount)
    {
        LineArithmetic.PercentageDiscount(amount, 15).ShouldBe(discount);
    }

    [Theory]
    [Trait("Requirement", "MNY-19")]
    [Trait("Requirement", "MNY-20")]
    [InlineData(1000, new long[] { 10000, 20000 }, new long[] { 333, 667 })]
    [InlineData(50000, new long[] { 10000, 20000 }, new long[] { 10000, 20000 })]
    [InlineData(1000, new long[] { 10000, -5000 }, new long[] { 1000, 0 })]
    public void FixedDiscounts_GoToPositiveLines_CappedAtTheirSum(long amountOff, long[] lines, long[] discounts)
    {
        LineArithmetic.FixedDiscount(amountOff, lines).ShouldBe(discounts);
    }

    [Theory]
    [Trait("Requirement", "MNY-22")]
    [Trait("Requirement", "MNY-23")]
    [InlineData(5000, 29900, null, 5000, 24900, 0)]
    [InlineData(50000, 29900, null, 29900, 0, 20100)]
    [InlineData(-1200, 29900, null, -1200, 31100, 0)]
    [InlineData(0, -15000, null, -15000, 0, 15000)]
    [InlineData(0, 50, 100L, 50, 0, -50)]
    [InlineData(-50, 29900, null, -50, 29950, 0)]
    public void BalanceVectors(long balance, long total, long? minimum, long applied, long due, long after)
    {
        var outcome = BalanceApplication.Apply(balance, total, minimum);

        (outcome.AppliedBalance, outcome.AmountDue, outcome.BalanceAfter).ShouldBe((applied, due, after));
        outcome.AppliedBalance.ShouldBe(-outcome.Changes.Sum(change => change.Amount));
    }

    [Fact]
    [Trait("Requirement", "MNY-21")]
    public void InvoiceTotals_AreSumsOfLines()
    {
        var kdv = new TaxRateSnapshot(20, Inclusive: true);
        var lines = new[]
        {
            new LineAmounts(29900, 4485, LineArithmetic.Tax(29900, 4485, kdv, false), true),
            new LineAmounts(-15000, -2250, LineArithmetic.Tax(-15000, -2250, kdv, false), true),
        };

        var totals = InvoiceTotals.Of(lines);

        totals.ShouldBe(new InvoiceTotals(14900, 2235, 4236 - 2125, 12665, 12665 - (4236 - 2125)));
    }
}
