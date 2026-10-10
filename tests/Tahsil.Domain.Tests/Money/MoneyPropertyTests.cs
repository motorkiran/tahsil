using CsCheck;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Tests.Money;

/// <summary>The money properties of the specification (§10), over generated amounts, weights, rates and balances.</summary>
public sealed class MoneyPropertyTests
{
    private static readonly Gen<long> Amounts = Gen.Long[-1_000_000_000_000L, 1_000_000_000_000L];
    private static readonly Gen<decimal> Rates = Gen.Int[0, 1_000_000].Select(rate => rate / 10_000m);

    [Fact]
    [Trait("Requirement", "MNY-6")]
    public void Allocation_SumsToTheAmount_WithinAUnitOfEachExactShare()
    {
        Gen.Select(Amounts, Gen.Long[0, 1_000_000_000].Array[1, 12].Where(weights => weights.Any(weight => weight > 0)))
            .Sample((amount, weights) =>
            {
                var parts = MoneyMath.Allocate(amount, weights);
                var total = weights.Aggregate(0m, (sum, weight) => sum + weight);
                return parts.Sum() == amount
                    && parts.Select((part, index) => Math.Abs(part - (amount * (decimal)weights[index] / total))).All(difference => difference < 1);
            });
    }

    [Fact]
    [Trait("Requirement", "MNY-16")]
    public void ACredit_IsTheExactOppositeOfTheMatchingCharge()
    {
        Gen.Select(Amounts, Rates, Gen.Bool, Gen.Int[0, 100]).Sample((amount, rate, inclusive, percentOff) =>
        {
            var chargeDiscount = LineArithmetic.PercentageDiscount(amount, percentOff);
            var creditDiscount = LineArithmetic.PercentageDiscount(-amount, percentOff);
            var charge = LineArithmetic.Tax(amount, chargeDiscount, new TaxRateSnapshot(rate, inclusive), false);
            var credit = LineArithmetic.Tax(-amount, creditDiscount, new TaxRateSnapshot(rate, inclusive), false);
            return creditDiscount == -chargeDiscount && credit == new LineTax(-charge.Taxable, -charge.Tax, -charge.Total, -charge.Net);
        });
    }

    [Fact]
    [Trait("Requirement", "MNY-14")]
    public void InclusiveTax_NetPlusTaxIsGross()
    {
        Gen.Select(Amounts, Rates).Sample((amount, rate) =>
        {
            var line = LineArithmetic.Tax(amount, 0, new TaxRateSnapshot(rate, true), false);
            return line.Net + line.Tax == line.Total && line.Total == amount;
        });
    }

    [Fact]
    [Trait("Requirement", "MNY-21")]
    public void InvoiceTotals_AreTheSumsOfTheirLines()
    {
        Gen.Select(Amounts, Rates, Gen.Bool).Array[0, 20].Sample(inputs =>
        {
            var lines = inputs.Select(input => new LineAmounts(input.Item1, 0, LineArithmetic.Tax(input.Item1, 0, new TaxRateSnapshot(input.Item2, input.Item3), false), input.Item3)).ToList();
            var totals = InvoiceTotals.Of(lines);
            return totals.Subtotal == lines.Sum(line => line.Amount)
                && totals.TaxTotal == lines.Sum(line => line.Tax.Tax)
                && totals.Total == lines.Sum(line => line.Tax.Total)
                && totals.TotalExcludingTax == lines.Sum(line => line.Tax.Taxable - (line.InclusiveTax ? line.Tax.Tax : 0));
        });
    }

    [Fact]
    [Trait("Requirement", "MNY-24")]
    public void TheBalance_FollowsItsChanges_AndTheAppliedBalanceIsTheirNegatedSum()
    {
        Gen.Select(Amounts, Amounts, Gen.Long[0, 10_000].Select(minimum => minimum == 0 ? (long?)null : minimum)).Sample((balance, total, minimum) =>
        {
            var outcome = BalanceApplication.Apply(balance, total, minimum);
            return outcome.BalanceAfter == balance + outcome.Changes.Sum(change => change.Amount)
                && outcome.AppliedBalance == -outcome.Changes.Sum(change => change.Amount)
                && outcome.AmountDue >= 0
                && (total < 0 || outcome.AmountDue + outcome.AppliedBalance == Math.Max(total, outcome.AppliedBalance));
        });
    }
}
