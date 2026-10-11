using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Model = Tahsil.Domain.Coupons;

namespace Tahsil.Application.Coupons;

/// <summary>Finds coupons and codes, and shows them as the API does.</summary>
internal static class CouponMapping
{
    public static Task<Model.Coupon?> FindCouponAsync(this ITahsilDbContext context, string idOrKey, CancellationToken cancellationToken) =>
        Guid.TryParse(idOrKey, out var id)
            ? context.Coupons.SingleOrDefaultAsync(coupon => coupon.Id == id, cancellationToken)
            : context.Coupons.SingleOrDefaultAsync(coupon => coupon.Key == idOrKey, cancellationToken);

    /// <summary>Coupons with the summaries of their products, loaded in one go.</summary>
    public static async Task<List<CouponResponse>> ToResponsesAsync(this ITahsilDbContext context, IReadOnlyCollection<Model.Coupon> coupons, CancellationToken cancellationToken)
    {
        var productIds = coupons.SelectMany(coupon => coupon.ProductIds ?? []).Distinct().ToList();
        var products = await context.Products.AsNoTracking().Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        return [.. coupons.Select(coupon => coupon.ToResponse(coupon.ProductIds?.Select(id => products[id].ToSummary()).ToList()))];
    }

    public static async Task<CouponResponse> ToResponseAsync(this ITahsilDbContext context, Model.Coupon coupon, CancellationToken cancellationToken) =>
        (await context.ToResponsesAsync([coupon], cancellationToken)).Single();

    public static CouponSummary ToSummary(this Model.Coupon coupon) => new(coupon.Id, coupon.Key, coupon.Name);

    public static PromotionCodeResponse ToResponse(this Model.PromotionCode code, Model.Coupon coupon) => new(
        code.Id,
        code.Code,
        coupon.ToSummary(),
        code.IsActive,
        code.ExpiresAt,
        code.MaxRedemptions,
        code.TimesRedeemed,
        code.FirstTimeOnly,
        code.MinimumAmount,
        code.MinimumAmountCurrency,
        code.CustomerId,
        code.CreatedAt,
        code.UpdatedAt);

    private static CouponResponse ToResponse(this Model.Coupon coupon, IReadOnlyList<ProductSummary>? products) => new(
        coupon.Id,
        coupon.Key,
        coupon.Name,
        coupon.PercentOff,
        coupon.AmountOff,
        coupon.Currency,
        coupon.Duration.Map<CouponDuration>(),
        coupon.DurationInCycles,
        products,
        coupon.MaxRedemptions,
        coupon.RedeemBy,
        coupon.TimesRedeemed,
        coupon.IsArchived,
        coupon.Metadata,
        coupon.CreatedAt,
        coupon.UpdatedAt);
}
