using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Coupons;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Coupons;

/// <summary>What a discount would apply to: the currency, the products bought and the subtotal before discounts.</summary>
public sealed record RedemptionTarget(string Currency, IReadOnlyCollection<Guid> ProductIds, long Subtotal);

/// <summary>A coupon, and the promotion code it was redeemed through, counted and locked in the caller's transaction.</summary>
public sealed record Redeemed(Coupon Coupon, PromotionCode? Code);

/// <summary>
/// Redeems coupons and promotion codes (CPN-7 to CPN-9): the coupon's and the code's rows are locked and their counts
/// incremented in the caller's transaction, so limits hold under concurrency; failures name the exact reason.
/// </summary>
public sealed class Redemptions(ITahsilDbContext context, IBillingLocks locks, IAuditLog audit, BillingClock clock)
{
    /// <summary>The coupon and code a request names by <c>coupon</c> or <c>promotionCode</c>, not yet redeemed.</summary>
    public async Task<Result<(Guid CouponId, Guid? CodeId)>> FindAsync(string? coupon, string? promotionCode, CancellationToken cancellationToken)
    {
        if (coupon is not null && promotionCode is not null)
        {
            return CouponErrors.Invalid("promotionCode", "Give a coupon or a promotion code, not both.");
        }

        if (promotionCode is not null)
        {
            var normalized = PromotionCode.Normalize(promotionCode);
            var code = await context.PromotionCodes.AsNoTracking()
                .Where(candidate => candidate.Code == normalized)
                .Select(candidate => new { candidate.Id, candidate.CouponId })
                .SingleOrDefaultAsync(cancellationToken);
            return code is null ? PromotionCodeErrors.NotFound : (code.CouponId, code.Id);
        }

        var found = await FindCouponAsync(coupon!, cancellationToken);
        return found is null ? CouponErrors.NotFound : (found.Id, (Guid?)null);
    }

    /// <summary>
    /// Counts a redemption by <paramref name="customerId"/> for <paramref name="target"/>, under the coupon's and the
    /// code's locks; the caller's transaction keeps the count only if what the discount is for is created too.
    /// </summary>
    public async Task<Result<Redeemed>> RedeemAsync(Guid couponId, Guid? codeId, Guid customerId, RedemptionTarget target, CancellationToken cancellationToken)
    {
        await locks.LockCouponAsync(couponId, cancellationToken);
        if (codeId is { } lockedCode)
        {
            await locks.LockPromotionCodeAsync(lockedCode, cancellationToken);
        }

        // Loaded under the locks, so that the counts are current.
        var coupon = await context.Coupons.SingleAsync(row => row.Id == couponId, cancellationToken);
        var code = codeId is { } id ? await context.PromotionCodes.SingleAsync(row => row.Id == id, cancellationToken) : null;
        var now = clock.GetUtcNow();
        if (coupon.AppliesTo(target.Currency, target.ProductIds) is { IsFailure: true } notApplicable)
        {
            return notApplicable.Error;
        }

        if (code is not null)
        {
            var hasPaidInvoice = code.FirstTimeOnly
                && await context.Invoices.AnyAsync(invoice => invoice.CustomerId == customerId && invoice.Status == InvoiceStatus.Paid, cancellationToken);
            if (code.Redeem(customerId, hasPaidInvoice, target.Subtotal, target.Currency, now) is { IsFailure: true } refused)
            {
                return refused.Error;
            }
        }

        if (coupon.Redeem(now) is { IsFailure: true } exhausted)
        {
            return exhausted.Error;
        }

        return new Redeemed(coupon, code);
    }

    /// <summary>
    /// Redeems a coupon or code for a subscription of <paramref name="items"/> and gives it the discount, which ends the
    /// one it had (CPN-10); the subtotal a code's minimum is compared with is a period of the licensed items.
    /// </summary>
    public async Task<Result> ApplyToSubscriptionAsync(
        Subscription subscription, (Guid CouponId, Guid? CodeId) found, IReadOnlyList<SubscriptionItemSpec> items, CancellationToken cancellationToken)
    {
        var target = new RedemptionTarget(
            subscription.Currency,
            [.. items.Select(item => item.Product.Id).Distinct()],
            items.Where(item => item.Quantity is not null).Sum(item => PriceAmounts.For(item.Price, item.Quantity!.Value)));
        var redeemed = await RedeemAsync(found.CouponId, found.CodeId, subscription.CustomerId, target, cancellationToken);
        if (redeemed.IsFailure)
        {
            return redeemed.Error;
        }

        var now = clock.GetUtcNow();
        await RemoveFromSubscriptionAsync(subscription, cancellationToken);
        var discount = Discount.ForSubscription(redeemed.Value.Coupon, redeemed.Value.Code, subscription.CustomerId, subscription.Id, now);
        Add(discount, redeemed.Value);
        subscription.SetDiscount(discount.Id, now);
        return Result.Success();
    }

    /// <summary>Ends a subscription's discount; invoices created afterwards are not discounted, and drafts drop it when finalized (CPN-14).</summary>
    public async Task RemoveFromSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        if (subscription.DiscountId is not { } id)
        {
            return;
        }

        var now = clock.GetUtcNow();
        if (await context.Discounts.SingleOrDefaultAsync(row => row.Id == id && row.EndedAt == null, cancellationToken) is { } current)
        {
            End(current, now);
        }

        subscription.SetDiscount(null, now);
    }

    /// <summary>Records a new discount and that it was applied.</summary>
    public void Add(Discount discount, Redeemed redeemed)
    {
        context.Discounts.Add(discount);
        audit.Record(AuditActions.DiscountApplied, AuditSubject.Of(AuditSubjects.Coupon, redeemed.Coupon.Id), new Dictionary<string, object?>
        {
            ["discount"] = discount.Id,
            ["promotionCode"] = redeemed.Code?.Code,
            ["subscription"] = discount.SubscriptionId,
            ["invoice"] = discount.InvoiceId,
        }, customerId: discount.CustomerId);
    }

    /// <summary>Ends a discount that is removed or replaced (CPN-10, CPN-14).</summary>
    private void End(Discount discount, DateTimeOffset now)
    {
        discount.End(now);
        audit.Record(AuditActions.DiscountRemoved, AuditSubject.Of(AuditSubjects.Coupon, discount.CouponId), new Dictionary<string, object?>
        {
            ["discount"] = discount.Id,
            ["subscription"] = discount.SubscriptionId,
        }, customerId: discount.CustomerId);
    }

    /// <summary>
    /// The coupon a manual draft is to be discounted by (CPN-13), checked as far as it can be before finalization
    /// redeems it: it exists, may still be redeemed, and an amount off is in the invoice's currency.
    /// </summary>
    public async Task<Result<InvoiceDiscount>> ForDraftAsync(string idOrKey, string currency, CancellationToken cancellationToken)
    {
        if (await FindCouponAsync(idOrKey, cancellationToken) is not { } coupon)
        {
            return CouponErrors.NotFound;
        }

        if (coupon.CanRedeem(clock.GetUtcNow()) is { IsFailure: true } refused)
        {
            return refused.Error;
        }

        return coupon.AmountOff is not null && coupon.Currency != currency ? CouponErrors.CurrencyMismatch : ToInvoiceDiscount(coupon, null, null);
    }

    /// <summary>A coupon by ID or key, untracked: redeeming it reads it again under its lock.</summary>
    private Task<Coupon?> FindCouponAsync(string idOrKey, CancellationToken cancellationToken) =>
        Guid.TryParse(idOrKey, out var id)
            ? context.Coupons.AsNoTracking().SingleOrDefaultAsync(coupon => coupon.Id == id, cancellationToken)
            : context.Coupons.AsNoTracking().SingleOrDefaultAsync(coupon => coupon.Key == idOrKey, cancellationToken);

    /// <summary>A coupon as invoices take it.</summary>
    public static InvoiceDiscount ToInvoiceDiscount(Coupon coupon, Guid? discountId, Guid? codeId) =>
        new(discountId, coupon.Id, codeId, coupon.Name, coupon.PercentOff, coupon.AmountOff, coupon.Currency, coupon.ProductIds);

    /// <summary>The discount a subscription's new invoices get now: its discount while it lasts (CPN-11).</summary>
    public async Task<InvoiceDiscount?> OfSubscriptionAsync(Guid? discountId, CancellationToken cancellationToken)
    {
        if (discountId is not { } id
            || await context.Discounts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.EndedAt == null, cancellationToken) is not { } discount)
        {
            return null;
        }

        var coupon = await context.Coupons.AsNoTracking().SingleAsync(row => row.Id == discount.CouponId, cancellationToken);
        return ToInvoiceDiscount(coupon, discount.Id, discount.PromotionCodeId);
    }
}
