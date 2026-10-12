using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Coupons;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Checkout;

/// <summary>
/// Discounts on checkout sessions (CHK-7, CHK-9): a coupon from the API or a code the customer enters changes the
/// quote when it could be redeemed, is checked again once the customer is known, and is counted when the session
/// completes (CPN-9).
/// </summary>
public sealed class CheckoutDiscounts(ITahsilDbContext context, Redemptions redemptions, ProviderRegistry registry, BillingClock clock)
{
    public static readonly Error NothingToDiscount = CheckoutErrors.Invalid("coupon", "A setup session sells nothing to discount.");

    public static readonly Error NothingLeftToPay = CheckoutErrors.Invalid(
        "coupon", "The discount leaves less to pay than the smallest amount the provider takes.");

    /// <summary>Discounts an open session by a coupon, through a code when given, if it could be redeemed for what the session sells.</summary>
    public async Task<Result> ApplyAsync(CheckoutSession session, Guid couponId, Guid? codeId, Guid? customerId, bool taxExempt, CancellationToken cancellationToken)
    {
        if (session.Mode == CheckoutMode.Setup)
        {
            return NothingToDiscount;
        }

        var allowed = await redemptions.CheckAsync(couponId, codeId, customerId, Target(session), cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var (discount, recurs) = allowed.Value;
        var quote = session.Quote.WithDiscount(session.Currency, taxExempt, discount, recurs);
        if (session.Subscription is not { HasTrial: true } && quote.AmountDue < await MinimumChargeAsync(session.Currency, cancellationToken))
        {
            return NothingLeftToPay;
        }

        return session.SetDiscount(discount, recurs, taxExempt, clock.GetUtcNow());
    }

    /// <summary>
    /// Checks the session's discount for the customer who submitted the form (CPN-5): a code for first purchases or
    /// for someone else is taken off the session, whose quote follows, and the refusal is returned.
    /// </summary>
    public async Task<Result> RecheckAsync(CheckoutSession session, Guid customerId, bool taxExempt, CancellationToken cancellationToken)
    {
        if (session.CouponId is not { } couponId)
        {
            return Result.Success();
        }

        // What the discount applies to is the quote without it.
        var allowed = await redemptions.CheckAsync(couponId, session.PromotionCodeId, customerId, Target(session), cancellationToken);
        if (allowed.IsSuccess)
        {
            return Result.Success();
        }

        session.SetDiscount(null, recurs: false, taxExempt, clock.GetUtcNow());
        return allowed.Error;
    }

    /// <summary>Counts the completed session's redemption (CPN-9); none without a discount.</summary>
    public async Task<Redeemed?> RedeemAsync(CheckoutSession session, Guid customerId, CancellationToken cancellationToken) =>
        session.CouponId is { } couponId
            ? await redemptions.RedeemForCheckoutAsync(couponId, session.PromotionCodeId, customerId, Target(session), cancellationToken)
            : null;

    /// <summary>What the session sells: its products and its subtotal before discounts, a period's for a trial.</summary>
    private static RedemptionTarget Target(CheckoutSession session)
    {
        var lines = session.Quote.Lines.Concat(session.Quote.Recurring?.Period.Lines ?? []).ToList();
        var billed = session.Quote.Lines.Count > 0 ? session.Quote : session.Quote.Recurring?.Period ?? session.Quote;
        return new RedemptionTarget(session.Currency, [.. lines.Where(line => line.ProductId is not null).Select(line => line.ProductId!.Value).Distinct()], billed.Subtotal);
    }

    private async Task<long> MinimumChargeAsync(string currency, CancellationToken cancellationToken) =>
        await context.RoutedConnectionAsync(currency, cancellationToken) is { } connection && registry.Find(connection.Provider) is { } provider
            ? Math.Max(1, provider.Descriptor.MinimumAmounts.GetValueOrDefault(currency))
            : 1;
}
