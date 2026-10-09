using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Model = Tahsil.Domain.Catalog;

namespace Tahsil.Application.Catalog;

public sealed class CreatePriceHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, BillingClock clock)
{
    public async Task<Result<Versioned<PriceResponse>>> HandleAsync(CreatePriceRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindProductAsync(request.Product, cancellationToken) is not { } product)
        {
            return Model.PriceErrors.InvalidReference("product", "No product has this ID or key.");
        }

        Model.TaxRate? taxRate = null;
        if (request.TaxRate is { } taxRateReference && (taxRate = await context.FindTaxRateAsync(taxRateReference, cancellationToken)) is null)
        {
            return Model.PriceErrors.InvalidReference("taxRate", "No tax rate has this ID or key.");
        }

        Model.Meter? meter = null;
        if (request.Meter is { } meterReference && (meter = await context.FindMeterAsync(meterReference, cancellationToken)) is null)
        {
            return Model.PriceErrors.InvalidReference("meter", "No meter has this ID or key.");
        }

        var recurring = request.Type == PriceType.Recurring;
        var now = clock.GetUtcNow();
        var created = Model.Price.Create(
            new Model.PriceDraft
            {
                Key = request.Key,
                Name = request.Name,
                Currency = request.Currency,
                Type = request.Type.Map<Model.PriceType>(),
                Interval = request.Interval.MapOptional<PriceInterval, Model.PriceInterval>(),
                IntervalCount = request.IntervalCount ?? (recurring && request.Interval is not null ? 1 : null),
                UsageType = request.UsageType.MapOptional<UsageType, Model.UsageType>() ?? (recurring ? Model.UsageType.Licensed : null),
                BillingScheme = request.BillingScheme.Map<Model.BillingScheme>(),
                UnitAmount = request.UnitAmount,
                UnitAmountDecimal = request.UnitAmountDecimal,
                PackageSize = request.PackageSize,
                PackageRounding = request.PackageRounding.MapOptional<PackageRounding, Model.PackageRounding>(),
                TiersMode = request.TiersMode.MapOptional<TiersMode, Model.TiersMode>(),
                Tiers = request.Tiers?.Select(tier => new Model.PriceTier(tier.UpTo, tier.UnitAmount, tier.UnitAmountDecimal, tier.FlatAmount)).ToList(),
                TrialDays = request.TrialDays,
                Metadata = request.Metadata,
            },
            product,
            taxRate,
            meter,
            now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var price = created.Value;
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        if (await PriceKeys.ClaimAsync(context, price.Id, price.Key, request.TransferKey, now, cancellationToken) is { } taken)
        {
            return taken;
        }

        context.Prices.Add(price);
        idempotency.RecordCreated(ResourceTypes.Price, price.Id);
        audit.Record(AuditActions.PriceCreated, AuditSubject.Of(AuditSubjects.Price, price.Id), new Dictionary<string, object?>
        {
            ["key"] = price.Key,
            ["product"] = product.Key,
            ["currency"] = price.Currency,
            ["unitAmount"] = price.UnitAmount,
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new Versioned<PriceResponse>(price.ToResponse(product, taxRate, meter), price.Version);
    }
}

public sealed class UpdatePriceHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, BillingClock clock)
{
    /// <summary>The request members of a new price that an update may not touch (DOM-5).</summary>
    private static readonly HashSet<string> ImmutableFields = new(StringComparer.Ordinal)
    {
        "product", "currency", "type", "interval", "intervalCount", "usageType", "billingScheme", "unitAmount", "unitAmountDecimal",
        "packageSize", "packageRounding", "tiersMode", "tiers", "meter", "taxRate",
    };

    public async Task<Result<Versioned<PriceResponse>>> HandleAsync(
        string idOrKey, UpdatePriceRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (request.Other?.Keys.FirstOrDefault() is { } other)
        {
            return ImmutableFields.Contains(other)
                ? Model.PriceErrors.Immutable(other)
                : Error.Validation(CommonErrors.InvalidRequestCode, $"'{other}' is not a field of a price.", other);
        }

        if (await context.FindPriceAsync(idOrKey, cancellationToken) is not { } price)
        {
            return Model.PriceErrors.NotFound;
        }

        if (Concurrency.Check(price, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var now = clock.GetUtcNow();
        var key = request.HasKey ? request.Key : price.Key;
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        if (key != price.Key && await PriceKeys.ClaimAsync(context, price.Id, key, request.TransferKey, now, cancellationToken) is { } taken)
        {
            return taken;
        }

        var updated = price.Update(
            key,
            request.HasName ? request.Name : price.Name,
            request.HasTrialDays ? request.TrialDays : price.TrialDays,
            request.IsArchived ?? price.IsArchived,
            request.HasMetadata ? request.Metadata : price.Metadata,
            now);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        audit.Record(AuditActions.PriceUpdated, AuditSubject.Of(AuditSubjects.Price, price.Id), Changes.Of(
            ("key", request.HasKey),
            ("name", request.HasName),
            ("trialDays", request.HasTrialDays),
            ("isArchived", request.IsArchived is not null),
            ("metadata", request.HasMetadata)));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new Versioned<PriceResponse>((await context.ToResponsesAsync([price], cancellationToken))[0], price.Version);
    }
}

public sealed class GetPriceHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<PriceResponse>>> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindPriceAsync(idOrKey, cancellationToken) is not { } price)
        {
            return Model.PriceErrors.NotFound;
        }

        return new Versioned<PriceResponse>((await context.ToResponsesAsync([price], cancellationToken))[0], price.Version);
    }
}

public sealed record ListPricesQuery(
    string? Product = null, string? Currency = null, PriceType? Type = null, bool? Archived = null, string? Cursor = null, int? Limit = null);

public sealed class ListPricesHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Model.Price, Guid> ByCreation = new("created", price => price.Id, price => price.Id);

    public async Task<Result<Page<PriceResponse>>> HandleAsync(ListPricesQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (CatalogLookup.ValidateLimit(query.Limit) is { } invalidLimit)
        {
            return invalidLimit;
        }

        var prices = context.Prices.AsNoTracking();
        if (query.Product is { } productReference)
        {
            if (await context.FindProductAsync(productReference, cancellationToken) is not { } product)
            {
                return Model.ProductErrors.NotFound;
            }

            prices = prices.Where(price => price.ProductId == product.Id);
        }

        if (query.Currency is { } currency)
        {
            prices = prices.Where(price => price.Currency == currency);
        }

        if (query.Type is { } type)
        {
            var domainType = type.Map<Model.PriceType>();
            prices = prices.Where(price => price.Type == domainType);
        }

        if (query.Archived is { } archived)
        {
            prices = prices.Where(price => price.IsArchived == archived);
        }

        var page = await ByCreation.ReadAsync(prices, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null
            ? CommonErrors.InvalidCursor
            : new Page<PriceResponse>(await context.ToResponsesAsync(page.Items, cancellationToken), page.NextCursor);
    }
}

/// <summary>Lookup keys move between prices only on request (CAT-10).</summary>
internal static class PriceKeys
{
    /// <summary>
    /// Makes <paramref name="key"/> free for the price <paramref name="priceId"/>: when another price holds it, takes it
    /// over with <paramref name="transfer"/>, saving the release first, as the key is unique at every moment; fails
    /// otherwise. Call inside the transaction that then saves the new holder.
    /// </summary>
    public static async Task<Error?> ClaimAsync(
        ITahsilDbContext context, Guid priceId, string? key, bool transfer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (key is null || await context.Prices.SingleOrDefaultAsync(price => price.Key == key && price.Id != priceId, cancellationToken) is not { } holder)
        {
            return null;
        }

        if (!transfer)
        {
            return Model.PriceErrors.KeyTaken;
        }

        holder.ReleaseKey(now);
        await context.SaveChangesAsync(cancellationToken);
        return null;
    }
}
