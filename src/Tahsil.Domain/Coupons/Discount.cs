using Tahsil.Domain.Invoices;

namespace Tahsil.Domain.Coupons;

/// <summary>
/// A coupon applied to a subscription or to one invoice (DOM §10.3). Created when the redemption is counted (CPN-9);
/// a subscription's discount lasts as its coupon's duration says (CPN-11, CPN-12), an invoice's applies to it alone
/// (CPN-13).
/// </summary>
public sealed class Discount
{
    private Discount()
    {
    }

    public Guid Id { get; private init; }

    public Guid CouponId { get; private init; }

    public Guid? PromotionCodeId { get; private init; }

    public Guid CustomerId { get; private init; }

    public Guid? SubscriptionId { get; private init; }

    public Guid? InvoiceId { get; private init; }

    public Guid? CheckoutSessionId { get; private init; }

    /// <summary>For <see cref="CouponDuration.Repeating"/>: the cycle invoices it still applies to.</summary>
    public int? CyclesRemaining { get; private set; }

    public DateTimeOffset StartedAt { get; private init; }

    public DateTimeOffset? EndedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public bool IsActive => EndedAt is null;

    public static Discount ForSubscription(Coupon coupon, PromotionCode? code, Guid customerId, Guid subscriptionId, DateTimeOffset now) =>
        New(coupon, code, customerId, subscriptionId, null, now);

    public static Discount ForInvoice(Coupon coupon, PromotionCode? code, Guid customerId, Guid invoiceId, DateTimeOffset now) =>
        New(coupon, code, customerId, null, invoiceId, now);

    /// <summary>
    /// An invoice was finalized with the discount (CPN-12, CPN-13): <c>once</c> and invoice discounts end, and
    /// <c>repeating</c> ones count a cycle invoice down and end after the last.
    /// </summary>
    public void RecordFinalized(Coupon coupon, BillingReason reason, DateTimeOffset now)
    {
        if (InvoiceId is not null || coupon.Duration == CouponDuration.Once)
        {
            End(now);
        }
        else if (coupon.Duration == CouponDuration.Repeating && reason is BillingReason.SubscriptionCreate or BillingReason.SubscriptionCycle)
        {
            CyclesRemaining--;
            if (CyclesRemaining <= 0)
            {
                End(now);
            }
        }
    }

    /// <summary>Removed, replaced (CPN-10) or used up.</summary>
    public void End(DateTimeOffset now) => EndedAt ??= now;

    private static Discount New(Coupon coupon, PromotionCode? code, Guid customerId, Guid? subscriptionId, Guid? invoiceId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        CouponId = coupon.Id,
        PromotionCodeId = code?.Id,
        CustomerId = customerId,
        SubscriptionId = subscriptionId,
        InvoiceId = invoiceId,
        CyclesRemaining = coupon.Duration == CouponDuration.Repeating ? coupon.DurationInCycles : null,
        StartedAt = now,
        CreatedAt = now,
    };
}
