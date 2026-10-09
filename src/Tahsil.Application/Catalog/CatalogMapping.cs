using Tahsil.Application.Common;
using Tahsil.Contracts;
using Model = Tahsil.Domain.Catalog;

namespace Tahsil.Application.Catalog;

internal static class CatalogMapping
{
    public static TaxRateResponse ToResponse(this Model.TaxRate rate) => new(
        rate.Id, rate.Key, rate.Name, rate.Description, rate.Percentage, rate.IsInclusive, rate.Country, rate.IsArchived, rate.CreatedAt, rate.UpdatedAt);

    public static TaxRateSummary ToSummary(this Model.TaxRate rate) => new(rate.Id, rate.Key, rate.Name, rate.Percentage, rate.IsInclusive);

    public static MeterResponse ToResponse(this Model.Meter meter) => new(
        meter.Id, meter.Key, meter.Name, meter.Aggregation.Map<MeterAggregation>(), meter.UniqueProperty, meter.IsArchived, meter.CreatedAt, meter.UpdatedAt);

    public static MeterSummary ToSummary(this Model.Meter meter) => new(meter.Id, meter.Key, meter.Name);

    public static ProductResponse ToResponse(this Model.Product product) => new(
        product.Id, product.Key, product.Name, product.Description, product.KimlikPlan, product.IsArchived, product.Metadata, product.CreatedAt, product.UpdatedAt);

    public static ProductSummary ToSummary(this Model.Product product) => new(product.Id, product.Key, product.Name);

    public static PriceResponse ToResponse(this Model.Price price, Model.Product product, Model.TaxRate? taxRate, Model.Meter? meter) => new(
        price.Id,
        price.Key,
        price.Name,
        product.ToSummary(),
        price.Currency,
        price.Type.Map<PriceType>(),
        price.Interval.MapOptional<Model.PriceInterval, PriceInterval>(),
        price.IntervalCount,
        price.UsageType.MapOptional<Model.UsageType, UsageType>(),
        price.BillingScheme.Map<BillingScheme>(),
        price.UnitAmount,
        price.UnitAmountDecimal,
        price.PackageSize,
        price.PackageRounding.MapOptional<Model.PackageRounding, PackageRounding>(),
        price.TiersMode.MapOptional<Model.TiersMode, TiersMode>(),
        price.Tiers?.Select(tier => new PriceTier(tier.UpTo, tier.UnitAmount, tier.UnitAmountDecimal, tier.FlatAmount)).ToList(),
        meter?.ToSummary(),
        price.TrialDays,
        taxRate?.ToSummary(),
        price.IsArchived,
        price.Metadata,
        price.CreatedAt,
        price.UpdatedAt);
}
