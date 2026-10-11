using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Model = Tahsil.Domain.Catalog;

namespace Tahsil.Application.Provisioning;

/// <summary>
/// Applies a provisioning document in one transaction, all of it or nothing, in dependency order (tax rates, meters,
/// products and their prices, coupons and their codes, provider connections and their currencies, webhook endpoints). Each item goes through the
/// regular create and update handlers, so validation, immutability, audit and the caller's permissions are those of the
/// API. A dry run reports the changes and keeps none.
/// </summary>
public sealed class ApplyProvisioningHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IIdempotency idempotency,
    CreateTaxRateHandler createTaxRate,
    UpdateTaxRateHandler updateTaxRate,
    CreateMeterHandler createMeter,
    UpdateMeterHandler updateMeter,
    CreateProductHandler createProduct,
    UpdateProductHandler updateProduct,
    CreatePriceHandler createPrice,
    UpdatePriceHandler updatePrice,
    CreateCouponHandler createCoupon,
    UpdateCouponHandler updateCoupon,
    CreatePromotionCodeHandler createPromotionCode,
    UpdatePromotionCodeHandler updatePromotionCode,
    IntegrationProvisioning integrations)
{
    public async Task<Result<ProvisioningResult>> HandleAsync(ProvisioningDocument document, bool dryRun, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ProvisioningWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        // The startup file's references are resolved before it gets here (StartupProvisioning); others are not allowed.
        if (ProvisioningSecrets.FindReference(document) is { } reference)
        {
            return reference;
        }

        // A document creates many resources; a retried request applies it again, which changes nothing.
        idempotency.Suppress();

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        var items = new List<ProvisionedItem>();

        foreach (var (index, rate) in (document.TaxRates ?? []).Index())
        {
            if (await Record(items, $"taxRates[{index}]", rate.Key, ApplyTaxRateAsync(rate, cancellationToken)) is { } error)
            {
                return error;
            }
        }

        foreach (var (index, meter) in (document.Meters ?? []).Index())
        {
            if (await Record(items, $"meters[{index}]", meter.Key, ApplyMeterAsync(meter, cancellationToken)) is { } error)
            {
                return error;
            }
        }

        foreach (var (index, product) in (document.Products ?? []).Index())
        {
            var path = $"products[{index}]";
            if (await Record(items, path, product.Key, ApplyProductAsync(product, cancellationToken)) is { } error)
            {
                return error;
            }

            var productItem = items.Count - 1;
            foreach (var (priceIndex, price) in (product.Prices ?? []).Index())
            {
                if (await Record(items, $"{path}.prices[{priceIndex}]", price.Key, ApplyPriceAsync(product.Key, price, cancellationToken)) is { } priceError)
                {
                    return priceError;
                }
            }

            // Archived only once its prices exist: an archived product takes no new prices.
            var archived = await ArchiveProductAsync(product, cancellationToken);
            if (archived.IsFailure)
            {
                return ProvisioningErrors.At(path, product.Key, archived.Error);
            }

            if (archived.Value && items[productItem].Result == ProvisioningItemResult.Unchanged)
            {
                items[productItem] = items[productItem] with { Result = ProvisioningItemResult.Updated };
            }
        }

        foreach (var (index, coupon) in (document.Coupons ?? []).Index())
        {
            var path = $"coupons[{index}]";
            if (await Record(items, path, coupon.Key, ApplyCouponAsync(coupon, cancellationToken)) is { } error)
            {
                return error;
            }

            foreach (var (codeIndex, code) in (coupon.PromotionCodes ?? []).Index())
            {
                if (await Record(items, $"{path}.promotionCodes[{codeIndex}]", code.Code, ApplyPromotionCodeAsync(coupon.Key, code, cancellationToken)) is { } codeError)
                {
                    return codeError;
                }
            }
        }

        var connectionItems = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (index, connection) in (document.ProviderConnections ?? []).Index())
        {
            if (await Record(items, $"providerConnections[{index}]", connection.Key, integrations.ApplyConnectionAsync(connection, cancellationToken)) is { } error)
            {
                return error;
            }

            connectionItems[connection.Key] = items.Count - 1;
        }

        var moved = await integrations.ApplyRoutingAsync(document.ProviderConnections ?? [], cancellationToken);
        if (moved.IsFailure)
        {
            return ProvisioningErrors.At("providerConnections", "currencies", moved.Error);
        }

        foreach (var key in moved.Value)
        {
            var item = connectionItems[key];
            if (items[item].Result == ProvisioningItemResult.Unchanged)
            {
                items[item] = items[item] with { Result = ProvisioningItemResult.Updated };
            }
        }

        foreach (var (index, endpoint) in (document.WebhookEndpoints ?? []).Index())
        {
            if (await Record(items, $"webhookEndpoints[{index}]", endpoint.Key, integrations.ApplyEndpointAsync(endpoint, cancellationToken)) is { } error)
            {
                return error;
            }
        }

        var result = new ProvisioningResult(
            items.Count(item => item.Result == ProvisioningItemResult.Created),
            items.Count(item => item.Result == ProvisioningItemResult.Updated),
            items.Count(item => item.Result == ProvisioningItemResult.Unchanged),
            items);

        if (dryRun)
        {
            // Disposing the transaction without committing rolls everything back.
            context.DiscardChanges();
            return result;
        }

        if (result.Created + result.Updated > 0)
        {
            audit.Record(AuditActions.ProvisioningApplied, data: new Dictionary<string, object?> { ["created"] = result.Created, ["updated"] = result.Updated });
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<Error?> Record(List<ProvisionedItem> items, string path, string key, Task<Result<ProvisioningItemResult>> apply)
    {
        var applied = await apply;
        if (applied.IsFailure)
        {
            return ProvisioningErrors.At(path, key, applied.Error);
        }

        items.Add(new ProvisionedItem(path, key, applied.Value));
        return null;
    }

    private async Task<Result<ProvisioningItemResult>> ApplyTaxRateAsync(ProvisionedTaxRate declared, CancellationToken cancellationToken)
    {
        var existing = await context.TaxRates.AsNoTracking().SingleOrDefaultAsync(rate => rate.Key == declared.Key, cancellationToken);
        if (existing is null)
        {
            var created = await createTaxRate.HandleAsync(
                new CreateTaxRateRequest
                {
                    Key = declared.Key,
                    Name = declared.Name,
                    Description = declared.Description,
                    Percentage = declared.Percentage,
                    Inclusive = declared.Inclusive,
                    Country = declared.Country,
                },
                cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            return declared.IsArchived
                ? Outcome(await updateTaxRate.HandleAsync(declared.Key, new UpdateTaxRateRequest { IsArchived = true }, null, cancellationToken), ProvisioningItemResult.Created)
                : ProvisioningItemResult.Created;
        }

        if (existing.Percentage != declared.Percentage)
        {
            return ProvisioningErrors.FixedProperty("percentage");
        }

        if (existing.IsInclusive != declared.Inclusive)
        {
            return ProvisioningErrors.FixedProperty("inclusive");
        }

        if (existing.Name == declared.Name.Trim() && existing.Description == Declared.Text(declared.Description) && existing.IsArchived == declared.IsArchived)
        {
            return ProvisioningItemResult.Unchanged;
        }

        return Outcome(
            await updateTaxRate.HandleAsync(
                declared.Key, new UpdateTaxRateRequest { Name = declared.Name, Description = declared.Description, IsArchived = declared.IsArchived }, null, cancellationToken),
            ProvisioningItemResult.Updated);
    }

    private async Task<Result<ProvisioningItemResult>> ApplyMeterAsync(ProvisionedMeter declared, CancellationToken cancellationToken)
    {
        var existing = await context.Meters.AsNoTracking().SingleOrDefaultAsync(meter => meter.Key == declared.Key, cancellationToken);
        if (existing is null)
        {
            var created = await createMeter.HandleAsync(
                new CreateMeterRequest { Key = declared.Key, Name = declared.Name, Aggregation = declared.Aggregation, UniqueProperty = declared.UniqueProperty },
                cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            return declared.IsArchived
                ? Outcome(await updateMeter.HandleAsync(declared.Key, new UpdateMeterRequest { IsArchived = true }, null, cancellationToken), ProvisioningItemResult.Created)
                : ProvisioningItemResult.Created;
        }

        if (existing.Aggregation.Map<MeterAggregation>() != declared.Aggregation)
        {
            return ProvisioningErrors.FixedProperty("aggregation");
        }

        if (existing.UniqueProperty != Declared.Text(declared.UniqueProperty))
        {
            return ProvisioningErrors.FixedProperty("uniqueProperty");
        }

        if (existing.Name == declared.Name.Trim() && existing.IsArchived == declared.IsArchived)
        {
            return ProvisioningItemResult.Unchanged;
        }

        return Outcome(
            await updateMeter.HandleAsync(declared.Key, new UpdateMeterRequest { Name = declared.Name, IsArchived = declared.IsArchived }, null, cancellationToken),
            ProvisioningItemResult.Updated);
    }

    private async Task<Result<ProvisioningItemResult>> ApplyProductAsync(ProvisionedProduct declared, CancellationToken cancellationToken)
    {
        var existing = await context.Products.AsNoTracking().SingleOrDefaultAsync(product => product.Key == declared.Key, cancellationToken);
        if (existing is null)
        {
            var created = await createProduct.HandleAsync(
                new CreateProductRequest
                {
                    Key = declared.Key,
                    Name = declared.Name,
                    Description = declared.Description,
                    KimlikPlan = declared.KimlikPlan,
                    Metadata = declared.Metadata,
                },
                cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            // Archived after its prices exist: an archived product takes no new prices.
            return ProvisioningItemResult.Created;
        }

        // Archiving waits for the prices (ArchiveProductAsync); un-archiving happens first, so new prices can be added.
        var unarchive = existing.IsArchived && !declared.IsArchived;
        if (existing.Name == declared.Name.Trim()
            && existing.Description == Declared.Text(declared.Description)
            && existing.KimlikPlan == Declared.Text(declared.KimlikPlan)
            && !unarchive
            && Declared.SameMetadata(existing.Metadata, declared.Metadata))
        {
            return ProvisioningItemResult.Unchanged;
        }

        return Outcome(
            await updateProduct.HandleAsync(
                declared.Key,
                new UpdateProductRequest
                {
                    Name = declared.Name,
                    Description = declared.Description,
                    KimlikPlan = declared.KimlikPlan,
                    Metadata = declared.Metadata,
                    IsArchived = unarchive ? false : null,
                },
                null,
                cancellationToken),
            ProvisioningItemResult.Updated);
    }

    /// <returns>Whether the product was archived now.</returns>
    private async Task<Result<bool>> ArchiveProductAsync(ProvisionedProduct declared, CancellationToken cancellationToken)
    {
        if (!declared.IsArchived || await context.Products.AsNoTracking().AnyAsync(product => product.Key == declared.Key && product.IsArchived, cancellationToken))
        {
            return false;
        }

        var archived = await updateProduct.HandleAsync(declared.Key, new UpdateProductRequest { IsArchived = true }, null, cancellationToken);
        return archived.IsFailure ? archived.Error : true;
    }

    private async Task<Result<ProvisioningItemResult>> ApplyPriceAsync(string productKey, ProvisionedPrice declared, CancellationToken cancellationToken)
    {
        var existing = await context.Prices.AsNoTracking().SingleOrDefaultAsync(price => price.Key == declared.Key, cancellationToken);
        if (existing is null)
        {
            var created = await createPrice.HandleAsync(
                new CreatePriceRequest
                {
                    Product = productKey,
                    Key = declared.Key,
                    Name = declared.Name,
                    Currency = declared.Currency,
                    Type = declared.Type,
                    Interval = declared.Interval,
                    IntervalCount = declared.IntervalCount,
                    UsageType = declared.UsageType,
                    BillingScheme = declared.BillingScheme,
                    UnitAmount = declared.UnitAmount,
                    UnitAmountDecimal = declared.UnitAmountDecimal,
                    PackageSize = declared.PackageSize,
                    PackageRounding = declared.PackageRounding,
                    TiersMode = declared.TiersMode,
                    Tiers = declared.Tiers,
                    Meter = declared.Meter,
                    TaxRate = declared.TaxRate,
                    TrialDays = declared.TrialDays,
                    Metadata = declared.Metadata,
                },
                cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            return declared.IsArchived
                ? Outcome(await updatePrice.HandleAsync(declared.Key, new UpdatePriceRequest { IsArchived = true }, null, cancellationToken), ProvisioningItemResult.Created)
                : ProvisioningItemResult.Created;
        }

        if (await FixedPropertyChangedAsync(existing, productKey, declared, cancellationToken) is { } changed)
        {
            return ProvisioningErrors.FixedProperty(changed);
        }

        if (existing.Name == Declared.Text(declared.Name)
            && existing.TrialDays == declared.TrialDays
            && existing.IsArchived == declared.IsArchived
            && Declared.SameMetadata(existing.Metadata, declared.Metadata))
        {
            return ProvisioningItemResult.Unchanged;
        }

        return Outcome(
            await updatePrice.HandleAsync(
                declared.Key,
                new UpdatePriceRequest { Name = declared.Name, TrialDays = declared.TrialDays, IsArchived = declared.IsArchived, Metadata = declared.Metadata },
                null,
                cancellationToken),
            ProvisioningItemResult.Updated);
    }

    /// <summary>The first property that differs from the stored price and cannot change (DOM-5), with the API's defaults applied.</summary>
    private async Task<string?> FixedPropertyChangedAsync(Model.Price existing, string productKey, ProvisionedPrice declared, CancellationToken cancellationToken)
    {
        var recurring = declared.Type == PriceType.Recurring;
        var product = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == existing.ProductId, cancellationToken);
        var taxRateKey = existing.TaxRateId is { } taxRateId
            ? await context.TaxRates.AsNoTracking().Where(rate => rate.Id == taxRateId).Select(rate => rate.Key).SingleAsync(cancellationToken)
            : null;
        var meterKey = existing.MeterId is { } meterId
            ? await context.Meters.AsNoTracking().Where(meter => meter.Id == meterId).Select(meter => meter.Key).SingleAsync(cancellationToken)
            : null;
        var declaredTiers = declared.Tiers?.Select(tier => new Model.PriceTier(tier.UpTo, tier.UnitAmount, tier.UnitAmountDecimal, tier.FlatAmount)).ToList();

        return product.Key != productKey ? "product"
            : existing.Currency != declared.Currency ? "currency"
            : existing.Type.Map<PriceType>() != declared.Type ? "type"
            : existing.Interval.MapOptional<Model.PriceInterval, PriceInterval>() != declared.Interval ? "interval"
            : existing.IntervalCount != (declared.IntervalCount ?? (recurring && declared.Interval is not null ? 1 : null)) ? "intervalCount"
            : existing.UsageType.MapOptional<Model.UsageType, UsageType>() != (declared.UsageType ?? (recurring ? UsageType.Licensed : null)) ? "usageType"
            : existing.BillingScheme.Map<BillingScheme>() != declared.BillingScheme ? "billingScheme"
            : existing.UnitAmount != declared.UnitAmount ? "unitAmount"
            : existing.UnitAmountDecimal != declared.UnitAmountDecimal ? "unitAmountDecimal"
            : existing.PackageSize != declared.PackageSize ? "packageSize"
            : existing.PackageRounding.MapOptional<Model.PackageRounding, PackageRounding>() != declared.PackageRounding ? "packageRounding"
            : existing.TiersMode.MapOptional<Model.TiersMode, TiersMode>() != declared.TiersMode ? "tiersMode"
            : !(existing.Tiers ?? []).SequenceEqual(declaredTiers ?? []) ? "tiers"
            : meterKey != declared.Meter ? "meter"
            : taxRateKey != declared.TaxRate ? "taxRate"
            : null;
    }

    private async Task<Result<ProvisioningItemResult>> ApplyCouponAsync(ProvisionedCoupon declared, CancellationToken cancellationToken)
    {
        var existing = await context.Coupons.AsNoTracking().SingleOrDefaultAsync(coupon => coupon.Key == declared.Key, cancellationToken);
        if (existing is null)
        {
            var created = await createCoupon.HandleAsync(
                new CreateCouponRequest
                {
                    Key = declared.Key,
                    Name = declared.Name,
                    PercentOff = declared.PercentOff,
                    AmountOff = declared.AmountOff,
                    Currency = declared.Currency,
                    Duration = declared.Duration,
                    DurationInCycles = declared.DurationInCycles,
                    Products = declared.Products,
                    MaxRedemptions = declared.MaxRedemptions,
                    RedeemBy = declared.RedeemBy,
                    Metadata = declared.Metadata,
                },
                cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            return declared.IsArchived
                ? Outcome(await updateCoupon.HandleAsync(declared.Key, new UpdateCouponRequest { IsArchived = true }, null, cancellationToken), ProvisioningItemResult.Created)
                : ProvisioningItemResult.Created;
        }

        var productKeys = existing.ProductIds is { } productIds
            ? await context.Products.AsNoTracking().Where(product => productIds.Contains(product.Id)).Select(product => product.Key).ToListAsync(cancellationToken)
            : null;
        var changed = existing.PercentOff != declared.PercentOff ? "percentOff"
            : existing.AmountOff != declared.AmountOff ? "amountOff"
            : existing.Currency != declared.Currency ? "currency"
            : existing.Duration.Map<CouponDuration>() != declared.Duration ? "duration"
            : existing.DurationInCycles != declared.DurationInCycles ? "durationInCycles"
            : !SameKeys(productKeys, declared.Products) ? "products"
            : null;
        if (changed is not null)
        {
            return ProvisioningErrors.FixedProperty(changed);
        }

        if (existing.Name == declared.Name.Trim()
            && existing.MaxRedemptions == declared.MaxRedemptions
            && existing.RedeemBy == declared.RedeemBy
            && existing.IsArchived == declared.IsArchived
            && Declared.SameMetadata(existing.Metadata, declared.Metadata))
        {
            return ProvisioningItemResult.Unchanged;
        }

        return Outcome(
            await updateCoupon.HandleAsync(
                declared.Key,
                new UpdateCouponRequest
                {
                    Name = declared.Name,
                    MaxRedemptions = declared.MaxRedemptions,
                    RedeemBy = declared.RedeemBy,
                    IsArchived = declared.IsArchived,
                    Metadata = declared.Metadata,
                },
                null,
                cancellationToken),
            ProvisioningItemResult.Updated);
    }

    private async Task<Result<ProvisioningItemResult>> ApplyPromotionCodeAsync(string couponKey, ProvisionedPromotionCode declared, CancellationToken cancellationToken)
    {
        var code = Domain.Coupons.PromotionCode.Normalize(declared.Code);
        var existing = await context.PromotionCodes.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Code == code, cancellationToken);
        if (existing is null)
        {
            var created = await createPromotionCode.HandleAsync(
                new CreatePromotionCodeRequest
                {
                    Coupon = couponKey,
                    Code = declared.Code,
                    ExpiresAt = declared.ExpiresAt,
                    MaxRedemptions = declared.MaxRedemptions,
                    FirstTimeOnly = declared.FirstTimeOnly,
                    MinimumAmount = declared.MinimumAmount,
                    MinimumAmountCurrency = declared.MinimumAmountCurrency,
                },
                cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            return declared.Active
                ? ProvisioningItemResult.Created
                : Outcome(await updatePromotionCode.HandleAsync(created.Value.Value.Id, new UpdatePromotionCodeRequest { IsActive = false }, null, cancellationToken), ProvisioningItemResult.Created);
        }

        var coupon = await context.Coupons.AsNoTracking().Where(candidate => candidate.Id == existing.CouponId).Select(candidate => candidate.Key).SingleAsync(cancellationToken);
        var changed = coupon != couponKey ? "coupon"
            : existing.ExpiresAt != declared.ExpiresAt ? "expiresAt"
            : existing.MaxRedemptions != declared.MaxRedemptions ? "maxRedemptions"
            : existing.FirstTimeOnly != declared.FirstTimeOnly ? "firstTimeOnly"
            : existing.MinimumAmount != declared.MinimumAmount ? "minimumAmount"
            : existing.MinimumAmountCurrency != declared.MinimumAmountCurrency ? "minimumAmountCurrency"
            : existing.CustomerId is not null ? "customer"
            : null;
        if (changed is not null)
        {
            return ProvisioningErrors.FixedProperty(changed);
        }

        return existing.IsActive == declared.Active
            ? ProvisioningItemResult.Unchanged
            : Outcome(await updatePromotionCode.HandleAsync(existing.Id, new UpdatePromotionCodeRequest { IsActive = declared.Active }, null, cancellationToken), ProvisioningItemResult.Updated);
    }

    private static bool SameKeys(IReadOnlyCollection<string>? stored, IReadOnlyCollection<string>? declared) =>
        stored is null ? declared is null : declared is not null && stored.ToHashSet(StringComparer.Ordinal).SetEquals(declared);

    private static Result<ProvisioningItemResult> Outcome<T>(Result<T> result, ProvisioningItemResult outcome) =>
        result.IsFailure ? result.Error : outcome;
}
