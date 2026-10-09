using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Tests.Catalog;

public sealed class CatalogRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("Requirement", "CAT-1")]
    [InlineData(-0.0001, false)]
    [InlineData(0, true)]
    [InlineData(20, true)]
    [InlineData(17.5555, true)]
    [InlineData(17.55555, false)]
    [InlineData(100, true)]
    [InlineData(100.0001, false)]
    public void TaxRatePercentages_AreBetweenZeroAndHundred_WithFourDecimals(double percentage, bool valid)
    {
        TaxRate.Create("kdv", "KDV", null, (decimal)percentage, isInclusive: true, null, Now).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    [Trait("Requirement", "DOM-2")]
    [Trait("Requirement", "CAT-2")]
    public void ATaxRate_KeepsItsPercentageAndInclusion()
    {
        var rate = TaxRate.Create("kdv-20", "KDV", null, 20m, isInclusive: true, "TR", Now).Value;

        rate.Update("KDV %20", "Standard rate", isArchived: true, Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        (rate.Name, rate.Description, rate.IsArchived, rate.Percentage, rate.IsInclusive).ShouldBe(("KDV %20", "Standard rate", true, 20m, true));
        typeof(TaxRate).GetProperties().Where(property => property.SetMethod?.IsPublic == true).ShouldBeEmpty();
    }

    [Theory]
    [Trait("Requirement", "CAT-16")]
    [InlineData(MeterAggregation.Sum, null, true)]
    [InlineData(MeterAggregation.UniqueCount, "user_id", true)]
    [InlineData(MeterAggregation.UniqueCount, null, false)]
    [InlineData(MeterAggregation.Count, "user_id", false)]
    public void UniqueCountMeters_NameTheirProperty(MeterAggregation aggregation, string? property, bool valid)
    {
        Meter.Create("api_calls", "API calls", aggregation, property, Now).IsSuccess.ShouldBe(valid);
    }

    [Theory]
    [Trait("Requirement", "CAT-4")]
    [InlineData("pro", true)]
    [InlineData("pro-monthly_try2", true)]
    [InlineData("Pro", false)]
    [InlineData("2pro", false)]
    [InlineData("pro plan", false)]
    [InlineData("", false)]
    public void Keys_AreLowercaseSlugs(string key, bool valid)
    {
        Product.Create(key, "Pro", null, null, null, Now).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    [Trait("Requirement", "CAT-4")]
    public void ProductText_IsTrimmed_AndEmptyMeansAbsent()
    {
        var product = Product.Create("pro", "  Pro  ", "   ", " starter ", null, Now).Value;

        (product.Name, product.Description, product.KimlikPlan).ShouldBe(("Pro", null, "starter"));
        product.Metadata.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Requirement", "MNY-1")]
    [InlineData("TRY", 2)]
    [InlineData("USD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    public void Currencies_HaveTheirIsoExponents(string code, int exponent)
    {
        Currency.Get(code).Exponent.ShouldBe(exponent);
    }

    [Theory]
    [Trait("Requirement", "MNY-1")]
    [InlineData("XAU")]
    [InlineData("CLF")]
    [InlineData("HRK")]
    [InlineData("try")]
    public void MetalsFundsAndWithdrawnCodes_AreNotSupported(string code)
    {
        Currency.TryGet(code, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Requirement", "DOM-1")]
    public void Metadata_IsLimited()
    {
        Metadata.Validate(Enumerable.Range(0, 51).ToDictionary(index => $"k{index}", _ => "v")).IsFailure.ShouldBeTrue();
        Metadata.Validate(new Dictionary<string, string> { [new string('k', 41)] = "v" }).IsFailure.ShouldBeTrue();
        Metadata.Validate(new Dictionary<string, string> { ["k"] = new string('v', 501) }).IsFailure.ShouldBeTrue();
        Metadata.Validate(new Dictionary<string, string> { ["order.id"] = "42" }).IsSuccess.ShouldBeTrue();
    }
}
