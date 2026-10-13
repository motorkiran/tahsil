using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Catalog;

public sealed class CreateProductHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, Kimlik.KimlikReferences kimlik, BillingClock clock)
{
    public async Task<Result<Versioned<ProductResponse>>> HandleAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var created = Product.Create(request.Key, request.Name, request.Description, request.KimlikPlan, request.Metadata, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await context.Products.AnyAsync(product => product.Key == request.Key, cancellationToken))
        {
            return ProductErrors.KeyTaken;
        }

        var product = created.Value;
        if (await kimlik.CheckPlanAsync(product.KimlikPlan, cancellationToken) is { IsFailure: true } unknown)
        {
            return unknown.Error;
        }

        context.Products.Add(product);
        idempotency.RecordCreated(ResourceTypes.Product, product.Id);
        audit.Record(AuditActions.ProductCreated, AuditSubject.Of(AuditSubjects.Product, product.Id), new Dictionary<string, object?> { ["key"] = product.Key });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return ProductErrors.KeyTaken;
        }

        return new Versioned<ProductResponse>(product.ToResponse(), product.Version);
    }
}

public sealed class UpdateProductHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IOutbox outbox,
    Kimlik.KimlikReferences kimlik,
    IOptions<Kimlik.KimlikOptions> kimlikOptions,
    BillingClock clock)
{
    public async Task<Result<Versioned<ProductResponse>>> HandleAsync(
        string idOrKey, UpdateProductRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindProductAsync(idOrKey, cancellationToken) is not { } product)
        {
            return ProductErrors.NotFound;
        }

        if (Concurrency.Check(product, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var plan = product.KimlikPlan;
        var updated = product.Update(
            request.Name ?? product.Name,
            request.HasDescription ? request.Description : product.Description,
            request.HasKimlikPlan ? request.KimlikPlan : product.KimlikPlan,
            request.IsArchived ?? product.IsArchived,
            request.HasMetadata ? request.Metadata : product.Metadata,
            clock.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        if (product.KimlikPlan != plan && await kimlik.CheckPlanAsync(product.KimlikPlan, cancellationToken) is { IsFailure: true } unknown)
        {
            return unknown.Error;
        }

        if (product.KimlikPlan != plan && kimlikOptions.Value.Enabled)
        {
            // The plan the product's subscriptions give in Kimlik changed (KML-16).
            await Kimlik.KimlikTriggers.QueueAsync(
                context,
                outbox,
                context.Subscriptions.Where(subscription => subscription.Items.Any(item => context.Prices.Any(price => price.Id == item.PriceId && price.ProductId == product.Id))),
                cancellationToken);
        }

        audit.Record(AuditActions.ProductUpdated, AuditSubject.Of(AuditSubjects.Product, product.Id), Changes.Of(
            ("name", request.Name is not null),
            ("description", request.HasDescription),
            ("kimlikPlan", request.HasKimlikPlan),
            ("isArchived", request.IsArchived is not null),
            ("metadata", request.HasMetadata)));
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<ProductResponse>(product.ToResponse(), product.Version);
    }
}

/// <summary>
/// Deletes a product and its prices while none of them was ever used (DOM-3); a used product is archived instead.
/// </summary>
public sealed class DeleteProductHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IEnumerable<IPriceUsage> usages)
{
    public async Task<Result> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindProductAsync(idOrKey, cancellationToken) is not { } product)
        {
            return ProductErrors.NotFound;
        }

        foreach (var usage in usages)
        {
            if (await usage.AnyPriceUsedAsync(product.Id, cancellationToken))
            {
                return ProductErrors.InUse;
            }
        }

        context.Products.Remove(product);
        audit.Record(AuditActions.ProductDeleted, AuditSubject.Of(AuditSubjects.Product, product.Id), new Dictionary<string, object?> { ["key"] = product.Key });
        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Tells whether a product's prices were used, by one kind of record that uses prices.</summary>
public interface IPriceUsage
{
    Task<bool> AnyPriceUsedAsync(Guid productId, CancellationToken cancellationToken);
}

public sealed class GetProductHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<ProductResponse>>> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.FindProductAsync(idOrKey, cancellationToken) is { } product
            ? new Versioned<ProductResponse>(product.ToResponse(), product.Version)
            : ProductErrors.NotFound;
    }
}

/// <summary>Products in creation order; <c>Search</c> matches the key or name, ignoring case.</summary>
public sealed record ListProductsQuery(bool? Archived = null, string? Search = null, string? Cursor = null, int? Limit = null);

public sealed class ListProductsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Product, Guid> ByCreation = new("created", product => product.Id, product => product.Id);

    public async Task<Result<Page<ProductResponse>>> HandleAsync(ListProductsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (CatalogLookup.ValidateLimit(query.Limit) is { } invalidLimit)
        {
            return invalidLimit;
        }

        var products = context.Products.AsNoTracking();
        if (query.Archived is { } archived)
        {
            products = products.Where(product => product.IsArchived == archived);
        }

        if (Text.Normalize(query.Search) is { } search)
        {
            var term = search.ToLowerInvariant();

            // Translated to SQL (lower() and LIKE), where neither the culture nor StringComparison applies.
#pragma warning disable CA1304, CA1311, CA1862
            products = products.Where(product => product.Key.Contains(term) || product.Name.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var page = await ByCreation.ReadAsync(products, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<ProductResponse>([.. page.Items.Select(product => product.ToResponse())], page.NextCursor);
    }
}
