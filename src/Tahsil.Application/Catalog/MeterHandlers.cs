using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Meter = Tahsil.Domain.Catalog.Meter;
using MeterErrors = Tahsil.Domain.Catalog.MeterErrors;

namespace Tahsil.Application.Catalog;

public sealed class CreateMeterHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, BillingClock clock)
{
    public async Task<Result<Versioned<MeterResponse>>> HandleAsync(CreateMeterRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var created = Meter.Create(
            request.Key, request.Name, request.Aggregation.Map<Domain.Catalog.MeterAggregation>(), request.UniqueProperty, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await context.Meters.AnyAsync(meter => meter.Key == request.Key, cancellationToken))
        {
            return MeterErrors.KeyTaken;
        }

        var meter = created.Value;
        context.Meters.Add(meter);
        idempotency.RecordCreated(ResourceTypes.Meter, meter.Id);
        audit.Record(AuditActions.MeterCreated, AuditSubject.Of(AuditSubjects.Meter, meter.Id), new Dictionary<string, object?> { ["key"] = meter.Key });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return MeterErrors.KeyTaken;
        }

        return new Versioned<MeterResponse>(meter.ToResponse(), meter.Version);
    }
}

public sealed class UpdateMeterHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, BillingClock clock)
{
    public async Task<Result<Versioned<MeterResponse>>> HandleAsync(
        string idOrKey, UpdateMeterRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindMeterAsync(idOrKey, cancellationToken) is not { } meter)
        {
            return MeterErrors.NotFound;
        }

        if (Concurrency.Check(meter, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var updated = meter.Update(request.Name ?? meter.Name, request.IsArchived ?? meter.IsArchived, clock.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        audit.Record(AuditActions.MeterUpdated, AuditSubject.Of(AuditSubjects.Meter, meter.Id), Changes.Of(
            ("name", request.Name is not null), ("isArchived", request.IsArchived is not null)));
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<MeterResponse>(meter.ToResponse(), meter.Version);
    }
}

public sealed class GetMeterHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<MeterResponse>>> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.FindMeterAsync(idOrKey, cancellationToken) is { } meter
            ? new Versioned<MeterResponse>(meter.ToResponse(), meter.Version)
            : MeterErrors.NotFound;
    }
}

public sealed class ListMetersHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Meter, Guid> ByCreation = new("created", meter => meter.Id, meter => meter.Id);

    public async Task<Result<Page<MeterResponse>>> HandleAsync(ListCatalogQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CatalogRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (CatalogLookup.ValidateLimit(query.Limit) is { } invalidLimit)
        {
            return invalidLimit;
        }

        var meters = context.Meters.AsNoTracking();
        if (query.Archived is { } archived)
        {
            meters = meters.Where(meter => meter.IsArchived == archived);
        }

        var page = await ByCreation.ReadAsync(meters, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<MeterResponse>([.. page.Items.Select(meter => meter.ToResponse())], page.NextCursor);
    }
}
