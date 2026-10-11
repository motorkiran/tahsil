using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Model = Tahsil.Domain.Coupons;

namespace Tahsil.Application.Coupons;

public sealed class CreatePromotionCodeHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, BillingClock clock)
{
    public async Task<Result<Versioned<PromotionCodeResponse>>> HandleAsync(CreatePromotionCodeRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindCouponAsync(request.Coupon, cancellationToken) is not { } coupon)
        {
            return Model.CouponErrors.NotFound;
        }

        if (request.Customer is { } customerId && !await context.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return CustomerErrors.NotFound;
        }

        var created = Model.PromotionCode.Create(
            coupon.Id,
            new Model.PromotionCodeDraft(
                request.Code, request.ExpiresAt, request.MaxRedemptions, request.FirstTimeOnly, request.MinimumAmount, request.MinimumAmountCurrency, request.Customer),
            clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        var code = created.Value;
        if (await context.PromotionCodes.AnyAsync(candidate => candidate.Code == code.Code, cancellationToken))
        {
            return Model.PromotionCodeErrors.CodeTaken;
        }

        context.PromotionCodes.Add(code);
        idempotency.RecordCreated(ResourceTypes.PromotionCode, code.Id);
        audit.Record(
            AuditActions.PromotionCodeCreated,
            AuditSubject.Of(AuditSubjects.PromotionCode, code.Id),
            new Dictionary<string, object?> { ["code"] = code.Code, ["coupon"] = coupon.Key });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return Model.PromotionCodeErrors.CodeTaken;
        }

        return new Versioned<PromotionCodeResponse>(code.ToResponse(coupon), code.Version);
    }
}

public sealed class UpdatePromotionCodeHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, BillingClock clock)
{
    public async Task<Result<Versioned<PromotionCodeResponse>>> HandleAsync(
        Guid id, UpdatePromotionCodeRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (request.Other?.Keys.FirstOrDefault() is { } other)
        {
            return Model.PromotionCodeErrors.Invalid(other, $"'{other}' cannot change; only isActive can. Create another code instead.");
        }

        if (await context.PromotionCodes.SingleOrDefaultAsync(code => code.Id == id, cancellationToken) is not { } code)
        {
            return Model.PromotionCodeErrors.NotFound;
        }

        if (Concurrency.Check(code, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        if (request.IsActive is { } isActive && isActive != code.IsActive)
        {
            code.SetActive(isActive, clock.GetUtcNow());
            audit.Record(
                AuditActions.PromotionCodeUpdated,
                AuditSubject.Of(AuditSubjects.PromotionCode, code.Id),
                new Dictionary<string, object?> { ["isActive"] = isActive });
            await context.SaveChangesAsync(cancellationToken);
        }

        var coupon = await context.Coupons.AsNoTracking().SingleAsync(candidate => candidate.Id == code.CouponId, cancellationToken);
        return new Versioned<PromotionCodeResponse>(code.ToResponse(coupon), code.Version);
    }
}

public sealed class GetPromotionCodeHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<PromotionCodeResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.PromotionCodes.AsNoTracking().SingleOrDefaultAsync(code => code.Id == id, cancellationToken) is not { } code)
        {
            return Model.PromotionCodeErrors.NotFound;
        }

        var coupon = await context.Coupons.AsNoTracking().SingleAsync(candidate => candidate.Id == code.CouponId, cancellationToken);
        return new Versioned<PromotionCodeResponse>(code.ToResponse(coupon), code.Version);
    }
}

/// <summary>Filters of the promotion code list: the coupon's ID or key, the active flag, and a code ignoring case.</summary>
public sealed record ListPromotionCodesQuery(string? Coupon = null, bool? Active = null, string? Code = null, string? Cursor = null, int? Limit = null);

public sealed class ListPromotionCodesHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Model.PromotionCode, Guid> Newest = new("newest", code => code.Id, code => code.Id, Descending: true);

    public async Task<Result<Page<PromotionCodeResponse>>> HandleAsync(ListPromotionCodesQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CouponsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (CatalogLookup.ValidateLimit(query.Limit) is { } invalidLimit)
        {
            return invalidLimit;
        }

        var codes = context.PromotionCodes.AsNoTracking();
        if (query.Coupon is not null)
        {
            var couponId = (await context.FindCouponAsync(query.Coupon, cancellationToken))?.Id;
            codes = codes.Where(code => code.CouponId == couponId);
        }

        if (query.Active is { } active)
        {
            codes = codes.Where(code => code.IsActive == active);
        }

        if (query.Code is not null)
        {
            var normalized = Model.PromotionCode.Normalize(query.Code);
            codes = codes.Where(code => code.Code == normalized);
        }

        var page = await Newest.ReadAsync(codes, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        if (page is null)
        {
            return CommonErrors.InvalidCursor;
        }

        var couponIds = page.Items.Select(code => code.CouponId).Distinct().ToList();
        var coupons = await context.Coupons.AsNoTracking().Where(coupon => couponIds.Contains(coupon.Id)).ToDictionaryAsync(coupon => coupon.Id, cancellationToken);
        return new Page<PromotionCodeResponse>([.. page.Items.Select(code => code.ToResponse(coupons[code.CouponId]))], page.NextCursor);
    }
}
