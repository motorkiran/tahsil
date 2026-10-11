using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Model = Tahsil.Domain.Coupons;

namespace Tahsil.Application.Coupons;

public sealed class CreateCouponHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, BillingClock clock)
{
    public async Task<Result<Versioned<CouponResponse>>> HandleAsync(CreateCouponRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        List<Guid>? productIds = null;
        if (request.Products is { } products)
        {
            productIds = [];
            foreach (var idOrKey in products)
            {
                if (await context.FindProductAsync(idOrKey, cancellationToken) is not { } product)
                {
                    return Model.CouponErrors.Invalid("products", $"There is no product '{idOrKey}'.");
                }

                productIds.Add(product.Id);
            }
        }

        var created = Model.Coupon.Create(
            new Model.CouponDraft(
                request.Key,
                request.Name,
                request.PercentOff,
                request.AmountOff,
                request.Currency,
                request.Duration.Map<Model.CouponDuration>(),
                request.DurationInCycles,
                productIds,
                request.MaxRedemptions,
                request.RedeemBy,
                request.Metadata),
            clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await context.Coupons.AnyAsync(coupon => coupon.Key == request.Key, cancellationToken))
        {
            return Model.CouponErrors.KeyTaken;
        }

        var coupon = created.Value;
        context.Coupons.Add(coupon);
        idempotency.RecordCreated(ResourceTypes.Coupon, coupon.Id);
        audit.Record(AuditActions.CouponCreated, AuditSubject.Of(AuditSubjects.Coupon, coupon.Id), new Dictionary<string, object?> { ["key"] = coupon.Key });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return Model.CouponErrors.KeyTaken;
        }

        return new Versioned<CouponResponse>(await context.ToResponseAsync(coupon, cancellationToken), coupon.Version);
    }
}

public sealed class UpdateCouponHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, BillingClock clock)
{
    private static readonly HashSet<string> ImmutableFields = new(StringComparer.Ordinal)
    {
        "key", "percentOff", "amountOff", "currency", "duration", "durationInCycles", "products",
    };

    public async Task<Result<Versioned<CouponResponse>>> HandleAsync(
        string idOrKey, UpdateCouponRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (request.Other?.Keys.FirstOrDefault() is { } other)
        {
            return ImmutableFields.Contains(other)
                ? Model.CouponErrors.Immutable(other)
                : Error.Validation(CommonErrors.InvalidRequestCode, $"'{other}' is not a field of a coupon.", other);
        }

        if (await context.FindCouponAsync(idOrKey, cancellationToken) is not { } coupon)
        {
            return Model.CouponErrors.NotFound;
        }

        if (Concurrency.Check(coupon, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var updated = coupon.Update(
            request.Name ?? coupon.Name,
            request.HasMaxRedemptions ? request.MaxRedemptions : coupon.MaxRedemptions,
            request.HasRedeemBy ? request.RedeemBy : coupon.RedeemBy,
            request.IsArchived ?? coupon.IsArchived,
            request.HasMetadata ? request.Metadata : coupon.Metadata,
            clock.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        audit.Record(AuditActions.CouponUpdated, AuditSubject.Of(AuditSubjects.Coupon, coupon.Id), Changes.Of(
            ("name", request.Name is not null),
            ("maxRedemptions", request.HasMaxRedemptions),
            ("redeemBy", request.HasRedeemBy),
            ("isArchived", request.IsArchived is not null),
            ("metadata", request.HasMetadata)));
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<CouponResponse>(await context.ToResponseAsync(coupon, cancellationToken), coupon.Version);
    }
}

public sealed class GetCouponHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<CouponResponse>>> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.FindCouponAsync(idOrKey, cancellationToken) is { } coupon
            ? new Versioned<CouponResponse>(await context.ToResponseAsync(coupon, cancellationToken), coupon.Version)
            : Model.CouponErrors.NotFound;
    }
}

public sealed class ListCouponsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Model.Coupon, Guid> Newest = new("newest", coupon => coupon.Id, coupon => coupon.Id, Descending: true);

    public async Task<Result<Page<CouponResponse>>> HandleAsync(ListCatalogQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (CatalogLookup.ValidateLimit(query.Limit) is { } invalidLimit)
        {
            return invalidLimit;
        }

        var coupons = context.Coupons.AsNoTracking();
        if (query.Archived is { } archived)
        {
            coupons = coupons.Where(coupon => coupon.IsArchived == archived);
        }

        var page = await Newest.ReadAsync(coupons, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<CouponResponse>(await context.ToResponsesAsync(page.Items, cancellationToken), page.NextCursor);
    }
}
