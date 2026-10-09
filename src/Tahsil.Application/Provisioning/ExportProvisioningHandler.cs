using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Model = Tahsil.Domain.Catalog;

namespace Tahsil.Application.Provisioning;

/// <summary>
/// The current catalog as a provisioning document, ordered by key, so it can be committed and applied elsewhere.
/// Prices without a lookup key cannot be provisioned and are left out.
/// </summary>
public sealed class ExportProvisioningHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<ProvisioningDocument>> HandleAsync(CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ProvisioningRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var taxRates = await context.TaxRates.AsNoTracking().OrderBy(rate => rate.Key).ToListAsync(cancellationToken);
        var meters = await context.Meters.AsNoTracking().OrderBy(meter => meter.Key).ToListAsync(cancellationToken);
        var products = await context.Products.AsNoTracking().OrderBy(product => product.Key).ToListAsync(cancellationToken);
        var prices = await context.Prices.AsNoTracking().Where(price => price.Key != null).OrderBy(price => price.Key).ToListAsync(cancellationToken);
        var taxRateKeys = taxRates.ToDictionary(rate => rate.Id, rate => rate.Key);
        var meterKeys = meters.ToDictionary(meter => meter.Id, meter => meter.Key);

        return new ProvisioningDocument
        {
            Schema = ProvisioningDocument.SchemaUrl,
            TaxRates = [.. taxRates.Select(rate => new ProvisionedTaxRate
            {
                Key = rate.Key,
                Name = rate.Name,
                Description = rate.Description,
                Percentage = rate.Percentage,
                Inclusive = rate.IsInclusive,
                Country = rate.Country,
                IsArchived = rate.IsArchived,
            })],
            Meters = [.. meters.Select(meter => new ProvisionedMeter
            {
                Key = meter.Key,
                Name = meter.Name,
                Aggregation = meter.Aggregation.Map<MeterAggregation>(),
                UniqueProperty = meter.UniqueProperty,
                IsArchived = meter.IsArchived,
            })],
            Products = [.. products.Select(product => new ProvisionedProduct
            {
                Key = product.Key,
                Name = product.Name,
                Description = product.Description,
                KimlikPlan = product.KimlikPlan,
                IsArchived = product.IsArchived,
                Metadata = product.Metadata.Count == 0 ? null : product.Metadata,
                Prices = [.. prices.Where(price => price.ProductId == product.Id).Select(price => ToProvisioned(price, taxRateKeys, meterKeys))],
            })],
        };
    }

    private static ProvisionedPrice ToProvisioned(Model.Price price, Dictionary<Guid, string> taxRateKeys, Dictionary<Guid, string> meterKeys) => new()
    {
        Key = price.Key!,
        Name = price.Name,
        Currency = price.Currency,
        Type = price.Type.Map<PriceType>(),
        Interval = price.Interval.MapOptional<Model.PriceInterval, PriceInterval>(),
        IntervalCount = price.IntervalCount,
        UsageType = price.UsageType.MapOptional<Model.UsageType, UsageType>(),
        BillingScheme = price.BillingScheme.Map<BillingScheme>(),
        UnitAmount = price.UnitAmount,
        UnitAmountDecimal = price.UnitAmountDecimal,
        PackageSize = price.PackageSize,
        PackageRounding = price.PackageRounding.MapOptional<Model.PackageRounding, PackageRounding>(),
        TiersMode = price.TiersMode.MapOptional<Model.TiersMode, TiersMode>(),
        Tiers = price.Tiers?.Select(tier => new PriceTier(tier.UpTo, tier.UnitAmount, tier.UnitAmountDecimal, tier.FlatAmount)).ToList(),
        Meter = price.MeterId is { } meterId ? meterKeys[meterId] : null,
        TaxRate = price.TaxRateId is { } taxRateId ? taxRateKeys[taxRateId] : null,
        TrialDays = price.TrialDays,
        IsArchived = price.IsArchived,
        Metadata = price.Metadata.Count == 0 ? null : price.Metadata,
    };
}
