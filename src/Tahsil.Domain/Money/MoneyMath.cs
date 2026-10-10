namespace Tahsil.Domain.Money;

/// <summary>
/// The arithmetic every amount goes through (MNY-5, MNY-6): intermediate values are <see cref="decimal"/>, each computed
/// value is rounded once, half away from zero, and splits use the largest remainder method so their parts add up.
/// </summary>
public static class MoneyMath
{
    /// <summary>A decimal amount in minor units as a whole number of them, half away from zero (−0.5 is −1).</summary>
    public static long Round(decimal minorUnits) => checked((long)Math.Round(minorUnits, 0, MidpointRounding.AwayFromZero));

    public static long Multiply(long amount, decimal factor) => Round(amount * factor);

    /// <summary>
    /// Splits <paramref name="amount"/> over <paramref name="weights"/> (not negative): each part is the whole part of its
    /// exact share, and the minor units left go one by one to the parts with the largest fractional remainders, the
    /// earlier first. Negative amounts split the same way with the sign kept. Parts always sum to the amount.
    /// </summary>
    public static long[] Allocate(long amount, IReadOnlyList<long> weights)
    {
        if (weights.Any(weight => weight < 0))
        {
            throw new ArgumentException("Weights cannot be negative.", nameof(weights));
        }

        var parts = new long[weights.Count];
        var total = weights.Aggregate(0m, (sum, weight) => sum + weight);
        if (weights.Count == 0 || total == 0)
        {
            if (amount != 0 && weights.Count > 0)
            {
                throw new ArgumentException("An amount cannot be split over weights that are all zero.", nameof(weights));
            }

            return parts;
        }

        var sign = Math.Sign(amount);
        var magnitude = Math.Abs((decimal)amount);
        var remainders = new decimal[weights.Count];
        long assigned = 0;
        for (var index = 0; index < weights.Count; index++)
        {
            var exact = magnitude * weights[index] / total;
            var whole = decimal.Floor(exact);
            parts[index] = (long)whole;
            remainders[index] = exact - whole;
            assigned += parts[index];
        }

        var left = (long)magnitude - assigned;
        foreach (var index in Enumerable.Range(0, weights.Count).OrderByDescending(index => remainders[index]).ThenBy(index => index).Take((int)left))
        {
            parts[index]++;
        }

        if (sign < 0)
        {
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index] = -parts[index];
            }
        }

        return parts;
    }
}
