using Tahsil.Domain.Catalog;

namespace Tahsil.Domain.Tests.Catalog;

[Trait("Requirement", "DOM-4")]
[Trait("Requirement", "CAT-8")]
public sealed class PriceShapeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly PriceTier[] Graduated =
    [
        new(1000, 10, null, null),
        new(10_000, null, 0.5m, 500),
        new(null, 0, null, null),
    ];

    public static TheoryData<string, PriceDraft, bool> ValidShapes => new()
    {
        { "one-time", OneTime(), false },
        { "recurring licensed per unit", Recurring() with { TrialDays = 14 }, false },
        { "recurring licensed package", Recurring() with { BillingScheme = BillingScheme.Package, PackageSize = 10, PackageRounding = PackageRounding.Up }, false },
        { "recurring licensed tiered", Recurring() with { UnitAmount = null, BillingScheme = BillingScheme.Tiered, TiersMode = TiersMode.Volume, Tiers = Graduated }, false },
        { "metered per unit", Metered(), true },
        { "metered fractional", Metered() with { UnitAmount = null, UnitAmountDecimal = 0.25m }, true },
        { "metered tiered", Metered() with { UnitAmount = null, BillingScheme = BillingScheme.Tiered, TiersMode = TiersMode.Graduated, Tiers = Graduated }, true },
        { "free", Recurring() with { UnitAmount = 0 }, false },
        { "365 days", Recurring() with { Interval = PriceInterval.Day, IntervalCount = 365 }, false },
        { "52 weeks", Recurring() with { Interval = PriceInterval.Week, IntervalCount = 52 }, false },
        { "12 months", Recurring() with { IntervalCount = 12 }, false },
        { "single unbounded tier", Recurring() with { UnitAmount = null, BillingScheme = BillingScheme.Tiered, TiersMode = TiersMode.Volume, Tiers = [new(null, 100, null, null)] }, false },
        { "key and name", OneTime() with { Key = "setup-fee", Name = "Setup" }, false },
    };

    public static TheoryData<string, PriceDraft, bool, string, string> InvalidShapes => new()
    {
        { "unknown currency", OneTime() with { Currency = "XYZ" }, false, "currency.unsupported", "currency" },
        { "lowercase currency", OneTime() with { Currency = "try" }, false, "currency.unsupported", "currency" },
        { "one-time with interval", OneTime() with { Interval = PriceInterval.Month, IntervalCount = 1 }, false, "price.invalid", "interval" },
        { "one-time with usage type", OneTime() with { UsageType = UsageType.Licensed }, false, "price.invalid", "usageType" },
        { "one-time with meter", OneTime(), true, "price.invalid", "usageType" },
        { "one-time package", OneTime() with { BillingScheme = BillingScheme.Package, PackageSize = 2, PackageRounding = PackageRounding.Up }, false, "price.invalid", "billingScheme" },
        { "one-time without amount", OneTime() with { UnitAmount = null }, false, "price.invalid", "unitAmount" },
        { "one-time fractional", OneTime() with { UnitAmount = null, UnitAmountDecimal = 0.5m }, false, "price.invalid", "unitAmount" },
        { "one-time with trial", OneTime() with { TrialDays = 7 }, false, "price.invalid", "trialDays" },
        { "recurring without interval", Recurring() with { Interval = null }, false, "price.invalid", "interval" },
        { "recurring with zero count", Recurring() with { IntervalCount = 0 }, false, "price.invalid", "interval" },
        { "longer than a year in days", Recurring() with { Interval = PriceInterval.Day, IntervalCount = 366 }, false, "price.invalid", "intervalCount" },
        { "longer than a year in weeks", Recurring() with { Interval = PriceInterval.Week, IntervalCount = 53 }, false, "price.invalid", "intervalCount" },
        { "longer than a year in months", Recurring() with { IntervalCount = 13 }, false, "price.invalid", "intervalCount" },
        { "two years", Recurring() with { Interval = PriceInterval.Year, IntervalCount = 2 }, false, "price.invalid", "intervalCount" },
        { "recurring without usage type", Recurring() with { UsageType = null }, false, "price.invalid", "usageType" },
        { "metered without meter", Metered(), false, "price.invalid", "meter" },
        { "licensed with meter", Recurring(), true, "price.invalid", "meter" },
        { "per unit with both amounts", Metered() with { UnitAmountDecimal = 0.5m }, true, "price.invalid", "unitAmount" },
        { "per unit without amount", Recurring() with { UnitAmount = null }, false, "price.invalid", "unitAmount" },
        { "licensed fractional", Recurring() with { UnitAmount = null, UnitAmountDecimal = 0.5m }, false, "price.invalid", "unitAmountDecimal" },
        { "per unit with tiers", Recurring() with { Tiers = Graduated }, false, "price.invalid", "billingScheme" },
        { "negative amount", Recurring() with { UnitAmount = -1 }, false, "price.invalid", "unitAmount" },
        { "too many decimals", Metered() with { UnitAmount = null, UnitAmountDecimal = 0.0000000000001m }, true, "price.invalid", "unitAmount" },
        { "package without size", Recurring() with { BillingScheme = BillingScheme.Package, PackageRounding = PackageRounding.Up }, false, "price.invalid", "packageSize" },
        { "package of one", Recurring() with { BillingScheme = BillingScheme.Package, PackageSize = 1, PackageRounding = PackageRounding.Up }, false, "price.invalid", "packageSize" },
        { "package without rounding", Recurring() with { BillingScheme = BillingScheme.Package, PackageSize = 10 }, false, "price.invalid", "packageRounding" },
        { "package without amount", Recurring() with { UnitAmount = null, BillingScheme = BillingScheme.Package, PackageSize = 10, PackageRounding = PackageRounding.Down }, false, "price.invalid", "unitAmount" },
        { "tiered with unit amount", Recurring() with { BillingScheme = BillingScheme.Tiered, TiersMode = TiersMode.Volume, Tiers = Graduated }, false, "price.invalid", "billingScheme" },
        { "tiered without mode", Recurring() with { UnitAmount = null, BillingScheme = BillingScheme.Tiered, Tiers = Graduated }, false, "price.invalid", "tiersMode" },
        { "tiered without tiers", Recurring() with { UnitAmount = null, BillingScheme = BillingScheme.Tiered, TiersMode = TiersMode.Volume, Tiers = [] }, false, "price.invalid", "tiers" },
        { "bounded last tier", Tiered(new PriceTier(100, 1, null, null)), false, "price.invalid", "tiers" },
        { "unbounded middle tier", Tiered(new PriceTier(null, 1, null, null), new PriceTier(null, 1, null, null)), false, "price.invalid", "tiers" },
        { "descending tiers", Tiered(new PriceTier(100, 1, null, null), new PriceTier(50, 1, null, null), new PriceTier(null, 1, null, null)), false, "price.invalid", "tiers" },
        { "zero bound", Tiered(new PriceTier(0, 1, null, null), new PriceTier(null, 1, null, null)), false, "price.invalid", "tiers" },
        { "tier with both amounts", Tiered(new PriceTier(null, 1, 0.5m, null)), false, "price.invalid", "tiers" },
        { "tier without amount", Tiered(new PriceTier(null, null, null, 100)), false, "price.invalid", "tiers" },
        { "negative flat amount", Tiered(new PriceTier(null, 1, null, -5)), false, "price.invalid", "tiers" },
        { "trial too long", Recurring() with { TrialDays = 731 }, false, "price.invalid", "trialDays" },
        { "zero trial", Recurring() with { TrialDays = 0 }, false, "price.invalid", "trialDays" },
        { "bad key", OneTime() with { Key = "Pro Monthly" }, false, "catalog.invalid_key", "key" },
        { "long name", OneTime() with { Name = new string('n', 101) }, false, "price.invalid", "name" },
        { "bad metadata", OneTime() with { Metadata = new Dictionary<string, string> { ["bad key"] = "x" } }, false, "metadata.invalid", "metadata" },
    };

    [Theory]
    [MemberData(nameof(ValidShapes))]
    public void ValidShapes_MakePrices(string shape, PriceDraft draft, bool withMeter)
    {
        var result = Price.Create(draft, Product(), TaxRate(), withMeter ? Meter() : null, Now);

        result.IsSuccess.ShouldBeTrue($"{shape}: {result.Error?.Code} {result.Error?.Field} {result.Error?.Message}");
        result.Value.Currency.ShouldBe(draft.Currency);
    }

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public void InvalidShapes_AreRefused_NamingTheField(string shape, PriceDraft draft, bool withMeter, string code, string field)
    {
        var result = Price.Create(draft, Product(), TaxRate(), withMeter ? Meter() : null, Now);

        result.IsFailure.ShouldBeTrue(shape);
        result.Error.Code.ShouldBe(code, shape);
        result.Error.Field.ShouldBe(field, shape);
    }

    [Theory]
    [Trait("Requirement", "CAT-11")]
    [InlineData("product")]
    [InlineData("taxRate")]
    [InlineData("meter")]
    public void ArchivedReferences_CannotTakeNewPrices(string archived)
    {
        var product = Product();
        var taxRate = TaxRate();
        var meter = Meter();
        _ = archived switch
        {
            "product" => product.Update(product.Name, null, null, isArchived: true, null, Now),
            "taxRate" => taxRate.Update(taxRate.Name, null, isArchived: true, Now),
            _ => meter.Update(meter.Name, isArchived: true, Now),
        };

        var result = Price.Create(Metered(), product, taxRate, meter, Now);

        result.Error.ShouldNotBeNull().Code.ShouldBe("price.invalid_reference");
        result.Error.Field.ShouldBe(archived);
    }

    [Fact]
    [Trait("Requirement", "DOM-5")]
    [Trait("Requirement", "CAT-9")]
    public void OnlyTheKeyNameTrialArchiveAndMetadata_Change()
    {
        var price = Price.Create(Recurring() with { Key = "pro-monthly-try", TrialDays = 14 }, Product(), TaxRate(), null, Now).Value;
        var later = Now.AddDays(1);

        price.Update("pro-monthly-try-v2", "Monthly", 30, isArchived: true, new Dictionary<string, string> { ["tier"] = "gold" }, later).IsSuccess.ShouldBeTrue();

        (price.Key, price.Name, price.TrialDays, price.IsArchived, price.Metadata["tier"], price.UpdatedAt)
            .ShouldBe(("pro-monthly-try-v2", "Monthly", (short?)30, true, "gold", later));
        (price.UnitAmount, price.Currency, price.Interval, price.CreatedAt).ShouldBe((29900L, "TRY", PriceInterval.Month, Now));
        typeof(Price).GetProperties().Where(property => property.SetMethod?.IsPublic == true).ShouldBeEmpty();
    }

    private static PriceDraft OneTime() => new() { Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 50000 };

    private static PriceDraft Recurring() => new()
    {
        Currency = "TRY",
        Type = PriceType.Recurring,
        Interval = PriceInterval.Month,
        IntervalCount = 1,
        UsageType = UsageType.Licensed,
        UnitAmount = 29900,
    };

    private static PriceDraft Metered() => Recurring() with { UsageType = UsageType.Metered, UnitAmount = 10 };

    private static PriceDraft Tiered(params PriceTier[] tiers) =>
        Recurring() with { UnitAmount = null, BillingScheme = BillingScheme.Tiered, TiersMode = TiersMode.Graduated, Tiers = tiers };

    private static Product Product() => Domain.Catalog.Product.Create("pro", "Pro", null, null, null, Now).Value;

    private static TaxRate TaxRate() => Domain.Catalog.TaxRate.Create("kdv-20", "KDV", null, 20m, isInclusive: true, "TR", Now).Value;

    private static Meter Meter() => Domain.Catalog.Meter.Create("api_calls", "API calls", MeterAggregation.Sum, null, Now).Value;
}
