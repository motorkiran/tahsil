using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Catalog;

public sealed class CreateTaxRateHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, BillingClock clock)
{
    public async Task<Result<Versioned<TaxRateResponse>>> HandleAsync(CreateTaxRateRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var created = TaxRate.Create(request.Key, request.Name, request.Description, request.Percentage, request.Inclusive, request.Country, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await context.TaxRates.AnyAsync(rate => rate.Key == request.Key, cancellationToken))
        {
            return TaxRateErrors.KeyTaken;
        }

        var rate = created.Value;
        context.TaxRates.Add(rate);
        idempotency.RecordCreated(ResourceTypes.TaxRate, rate.Id);
        audit.Record(AuditActions.TaxRateCreated, AuditSubject.Of(AuditSubjects.TaxRate, rate.Id), new Dictionary<string, object?> { ["key"] = rate.Key });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return TaxRateErrors.KeyTaken;
        }

        return new Versioned<TaxRateResponse>(rate.ToResponse(), rate.Version);
    }
}

public sealed class UpdateTaxRateHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, BillingClock clock)
{
    public async Task<Result<Versioned<TaxRateResponse>>> HandleAsync(
        string idOrKey, UpdateTaxRateRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindTaxRateAsync(idOrKey, cancellationToken) is not { } rate)
        {
            return TaxRateErrors.NotFound;
        }

        if (Concurrency.Check(rate, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var updated = rate.Update(
            request.Name ?? rate.Name,
            request.HasDescription ? request.Description : rate.Description,
            request.IsArchived ?? rate.IsArchived,
            clock.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        audit.Record(AuditActions.TaxRateUpdated, AuditSubject.Of(AuditSubjects.TaxRate, rate.Id), Changes.Of(
            ("name", request.Name is not null), ("description", request.HasDescription), ("isArchived", request.IsArchived is not null)));
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<TaxRateResponse>(rate.ToResponse(), rate.Version);
    }
}

public sealed class GetTaxRateHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<TaxRateResponse>>> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.FindTaxRateAsync(idOrKey, cancellationToken) is { } rate
            ? new Versioned<TaxRateResponse>(rate.ToResponse(), rate.Version)
            : TaxRateErrors.NotFound;
    }
}

public sealed record ListCatalogQuery(bool? Archived = null, string? Cursor = null, int? Limit = null);

public sealed class ListTaxRatesHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<TaxRate, Guid> ByCreation = new("created", rate => rate.Id, rate => rate.Id);

    public async Task<Result<Page<TaxRateResponse>>> HandleAsync(ListCatalogQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (CatalogLookup.ValidateLimit(query.Limit) is { } invalidLimit)
        {
            return invalidLimit;
        }

        var rates = context.TaxRates.AsNoTracking();
        if (query.Archived is { } archived)
        {
            rates = rates.Where(rate => rate.IsArchived == archived);
        }

        var page = await ByCreation.ReadAsync(rates, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<TaxRateResponse>([.. page.Items.Select(rate => rate.ToResponse())], page.NextCursor);
    }
}
