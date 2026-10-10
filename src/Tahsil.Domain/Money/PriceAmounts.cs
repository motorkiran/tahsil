using Tahsil.Domain.Catalog;

namespace Tahsil.Domain.Money;

/// <summary>How a price turns a quantity into an amount in minor units (MNY-7 to MNY-11), rounded once at the end.</summary>
public static class PriceAmounts
{
    public static long For(Price price, decimal quantity) => For(
        price.BillingScheme, price.UnitAmount, price.UnitAmountDecimal, price.PackageSize, price.PackageRounding, price.TiersMode, price.Tiers, quantity);

    public static long For(
        BillingScheme scheme,
        long? unitAmount,
        decimal? unitAmountDecimal,
        int? packageSize,
        PackageRounding? packageRounding,
        TiersMode? tiersMode,
        IReadOnlyList<PriceTier>? tiers,
        decimal quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        if (quantity == 0)
        {
            return 0;
        }

        return scheme switch
        {
            BillingScheme.PerUnit => MoneyMath.Round(quantity * Unit(unitAmount, unitAmountDecimal)),
            BillingScheme.Package => Package(quantity, packageSize!.Value, packageRounding!.Value, unitAmount!.Value),
            _ => tiersMode == TiersMode.Volume ? Volume(quantity, tiers!) : Graduated(quantity, tiers!),
        };
    }

    private static long Package(decimal quantity, int size, PackageRounding rounding, long unitAmount)
    {
        var packages = quantity / size;
        packages = rounding == PackageRounding.Up ? decimal.Ceiling(packages) : decimal.Floor(packages);
        return checked((long)packages * unitAmount);
    }

    /// <summary>Each unit is priced by the tier it falls in; a tier's flat amount counts once any unit falls in it.</summary>
    private static long Graduated(decimal quantity, IReadOnlyList<PriceTier> tiers)
    {
        var total = 0m;
        var below = 0m;
        foreach (var tier in tiers)
        {
            if (quantity <= below)
            {
                break;
            }

            var top = tier.UpTo is { } upTo ? Math.Min(quantity, upTo) : quantity;
            var units = top - below;
            total += units * Unit(tier.UnitAmount, tier.UnitAmountDecimal) + (tier.FlatAmount ?? 0);
            below = top;
        }

        return MoneyMath.Round(total);
    }

    /// <summary>Every unit is priced by the tier the whole quantity falls in; <c>upTo</c> is inclusive.</summary>
    private static long Volume(decimal quantity, IReadOnlyList<PriceTier> tiers)
    {
        var tier = tiers.First(candidate => candidate.UpTo is not { } upTo || quantity <= upTo);
        return MoneyMath.Round(quantity * Unit(tier.UnitAmount, tier.UnitAmountDecimal) + (tier.FlatAmount ?? 0));
    }

    private static decimal Unit(long? unitAmount, decimal? unitAmountDecimal) =>
        unitAmount ?? unitAmountDecimal ?? throw new InvalidOperationException("A price or tier has no unit amount.");
}
