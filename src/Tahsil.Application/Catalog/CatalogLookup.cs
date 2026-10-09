using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Catalog;

/// <summary>Finds catalog objects by ID or key, as every catalog reference in the API may be either (API-4).</summary>
internal static class CatalogLookup
{
    public static Task<TaxRate?> FindTaxRateAsync(this ITahsilDbContext context, string idOrKey, CancellationToken cancellationToken) =>
        Guid.TryParse(idOrKey, out var id)
            ? context.TaxRates.SingleOrDefaultAsync(rate => rate.Id == id, cancellationToken)
            : context.TaxRates.SingleOrDefaultAsync(rate => rate.Key == idOrKey, cancellationToken);

    public static Task<Meter?> FindMeterAsync(this ITahsilDbContext context, string idOrKey, CancellationToken cancellationToken) =>
        Guid.TryParse(idOrKey, out var id)
            ? context.Meters.SingleOrDefaultAsync(meter => meter.Id == id, cancellationToken)
            : context.Meters.SingleOrDefaultAsync(meter => meter.Key == idOrKey, cancellationToken);

    public static Task<Product?> FindProductAsync(this ITahsilDbContext context, string idOrKey, CancellationToken cancellationToken) =>
        Guid.TryParse(idOrKey, out var id)
            ? context.Products.SingleOrDefaultAsync(product => product.Id == id, cancellationToken)
            : context.Products.SingleOrDefaultAsync(product => product.Key == idOrKey, cancellationToken);

    public static Task<Price?> FindPriceAsync(this ITahsilDbContext context, string idOrKey, CancellationToken cancellationToken) =>
        Guid.TryParse(idOrKey, out var id)
            ? context.Prices.SingleOrDefaultAsync(price => price.Id == id, cancellationToken)
            : context.Prices.SingleOrDefaultAsync(price => price.Key == idOrKey, cancellationToken);

    /// <summary>Prices as the API shows them, with their product, tax rate and meter summaries, loaded in one go.</summary>
    public static async Task<List<Contracts.PriceResponse>> ToResponsesAsync(this ITahsilDbContext context, IReadOnlyCollection<Price> prices, CancellationToken cancellationToken)
    {
        var productIds = prices.Select(price => price.ProductId).Distinct().ToList();
        var taxRateIds = prices.Where(price => price.TaxRateId is not null).Select(price => price.TaxRateId!.Value).Distinct().ToList();
        var meterIds = prices.Where(price => price.MeterId is not null).Select(price => price.MeterId!.Value).Distinct().ToList();

        var products = await context.Products.AsNoTracking().Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        var taxRates = await context.TaxRates.AsNoTracking().Where(rate => taxRateIds.Contains(rate.Id)).ToDictionaryAsync(rate => rate.Id, cancellationToken);
        var meters = await context.Meters.AsNoTracking().Where(meter => meterIds.Contains(meter.Id)).ToDictionaryAsync(meter => meter.Id, cancellationToken);

        return [.. prices.Select(price => price.ToResponse(
            products[price.ProductId],
            price.TaxRateId is { } taxRateId ? taxRates[taxRateId] : null,
            price.MeterId is { } meterId ? meters[meterId] : null))];
    }

    public static Error? ValidateLimit(int? limit) => Paging.IsValidLimit(limit ?? Paging.DefaultLimit) ? null : CommonErrors.InvalidLimit();
}
