using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Application.Invoices;
using Tahsil.Application.Notifications;
using Tahsil.Application.Subscriptions;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Portal;
using Tahsil.Domain.Subscriptions;
using CancellationFeedback = Tahsil.Contracts.CancellationFeedback;
using CancellationReason = Tahsil.Contracts.CancellationReason;
using CustomerType = Tahsil.Domain.Customers.CustomerType;
using SubscriptionStatus = Tahsil.Domain.Subscriptions.SubscriptionStatus;

namespace Tahsil.Application.Portal;

/// <summary>How a subscription can end in the portal (PRT-8, PRT-10).</summary>
public enum CancelKind
{
    /// <summary>At the end of the period, or of the trial.</summary>
    PeriodEnd,

    /// <summary>Now, with the unused share of the period refunded (SUB-31).</summary>
    Now,

    /// <summary>The right of withdrawal: now, with everything paid for the subscription refunded (PRT-10).</summary>
    Withdrawal,
}

/// <summary>One way to end the subscription, as the page states it and the record keeps it.</summary>
public sealed record CancelChoice(CancelKind Kind, string Text, long Refund, DateTimeOffset EndsAt);

/// <summary>What the customer may choose when ending a subscription, and a retention offer when one is configured.</summary>
public sealed record CancelChoices(Subscription Subscription, string Plan, IReadOnlyList<CancelChoice> Choices, string? RetentionOffer);

public static class PortalCancelErrors
{
    public static readonly Error NotOffered = Error.Validation("portal.cancel_not_offered", "This way of ending the subscription is not offered.");
}

/// <summary>
/// Ending a subscription in the portal (PRT §4, §5): one optional question and one confirmation, never more steps than
/// subscribing took. Individuals choose between the period's end and now with a prorated refund (SUB-35); companies get
/// <c>Tahsil:Portal:CompanyCancellation</c>; with the <c>withdrawal</c> feature, an individual who did not consent to
/// the service starting at once may withdraw within 14 days. Each choice is recorded as shown and confirmed by email.
/// </summary>
public sealed class PortalCancellation(
    ITahsilDbContext context,
    IPageCaller caller,
    CancelSubscriptionHandler cancel,
    ResumeSubscriptionHandler resume,
    UpdateSubscriptionHandler update,
    SubscriptionLines lines,
    IOutbox outbox,
    IOptions<PortalOptions> portal,
    IOptions<BillingOptions> billing,
    BillingClock clock)
{
    /// <summary>The statutory period of withdrawal from a distance contract.</summary>
    public static readonly TimeSpan WithdrawalPeriod = TimeSpan.FromDays(14);

    /// <summary>The choices for a running subscription of the session's customer that is not already ending; none otherwise.</summary>
    public async Task<CancelChoices?> ChoicesAsync(PortalSession session, Guid subscriptionId, string locale, CancellationToken cancellationToken)
    {
        Use(session);
        var options = portal.Value;
        if (!options.IsEnabled(PortalFeatures.Cancel)
            || await context.Subscriptions.AsNoTracking().Include(row => row.Items)
                .SingleOrDefaultAsync(row => row.Id == subscriptionId && row.CustomerId == session.CustomerId, cancellationToken)
                is not { IsTerminal: false, CancelAt: null, Status: not SubscriptionStatus.Incomplete } subscription)
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == session.CustomerId, cancellationToken);
        var now = clock.GetUtcNow();
        var culture = Formatting.CultureFor(locale);
        var exponent = Domain.Money.Currency.TryGet(subscription.Currency, out var currency) ? currency.Exponent : 2;
        string Money(long amount) => Formatting.Money(amount, subscription.Currency, exponent, culture);
        var turkish = locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        var trial = subscription is { Status: SubscriptionStatus.Trialing, TrialEnded: false };
        var periodEnd = trial ? subscription.TrialEnd!.Value : subscription.CurrentPeriodEnd;
        var date = Formatting.Date(periodEnd, billing.Value.Zone, culture);

        var choices = new List<CancelChoice>
        {
            new(CancelKind.PeriodEnd, (trial, turkish) switch
            {
                (true, true) => $"Deneme süremin sonunda ({date})",
                (true, false) => $"At the end of my trial ({date})",
                (false, true) => $"Dönemin sonunda ({date})",
                _ => $"At the end of the period ({date})",
            }, 0, periodEnd),
        };

        var individual = customer.Type == CustomerType.Individual;
        if (individual || options.CompanyCancellation == "both")
        {
            var refund = await cancel.RefundPreviewAsync(subscription.Id, CancellationRefund.Prorated, cancellationToken);
            choices.Add(new CancelChoice(CancelKind.Now, (refund > 0, turkish) switch
            {
                (true, true) => $"Hemen, kullanılmayan süre için {Money(refund)} iadeyle",
                (true, false) => $"Now, with a refund of {Money(refund)} for the unused time",
                (false, true) => "Hemen",
                _ => "Now",
            }, refund, now));
        }

        if (individual && options.IsEnabled(PortalFeatures.Withdrawal) && await MayWithdrawAsync(subscription, now, cancellationToken))
        {
            var refund = await cancel.RefundPreviewAsync(subscription.Id, CancellationRefund.AllPayments, cancellationToken);
            choices.Add(new CancelChoice(
                CancelKind.Withdrawal,
                turkish
                    ? $"Cayma hakkımı kullanıyorum: aboneliğim hemen sona ersin ve ödediğim {Money(refund)} iade edilsin"
                    : $"I withdraw: end my subscription now and refund the {Money(refund)} I paid",
                refund,
                now));
        }

        return new CancelChoices(
            subscription,
            await SubscriptionCanceledComposer.PlanAsync(lines, subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken),
            choices,
            await RetentionOfferAsync(subscription, locale, cancellationToken));
    }

    /// <summary>Ends the subscription as chosen (PRT-8 to PRT-10): recorded as shown, confirmed by email, refunded as stated.</summary>
    public async Task<Result> CancelAsync(
        PortalSession session, Guid subscriptionId, CancelKind kind, CancellationFeedback? feedback, string locale, IPAddress? address, string? userAgent, CancellationToken cancellationToken)
    {
        if (await ChoicesAsync(session, subscriptionId, locale, cancellationToken) is not { } choices
            || choices.Choices.FirstOrDefault(candidate => candidate.Kind == kind) is not { } choice)
        {
            return PortalCancelErrors.NotOffered;
        }

        var subscription = choices.Subscription;
        context.ConsentRecords.Add(ConsentRecord.ForPortal(
            session.CustomerId,
            kind == CancelKind.Withdrawal ? ConsentType.Withdrawal : ConsentType.Cancellation,
            choice.Text,
            new Dictionary<string, string>
            {
                ["choice"] = kind.ToString(),
                ["refund"] = choice.Refund.ToString(CultureInfo.InvariantCulture),
                ["endsAt"] = choice.EndsAt.ToString("O", CultureInfo.InvariantCulture),
                ["currency"] = subscription.Currency,
                ["feedback"] = feedback?.ToString() ?? string.Empty,
            },
            session.Id,
            subscription.Id,
            address,
            userAgent,
            clock.GetUtcNow()));
        if (kind == CancelKind.Withdrawal)
        {
            CustomerEmails.Enqueue(outbox, EmailTypes.WithdrawalConfirmed, subscription.Id, subscription.Id.ToString(), new Dictionary<string, string>
            {
                ["refund"] = choice.Refund.ToString(CultureInfo.InvariantCulture),
            });
        }

        // The record and the email are saved with the cancellation, in its transaction.
        var canceled = await cancel.HandleAsync(subscription.Id, new CancelSubscriptionRequest
        {
            At = kind == CancelKind.PeriodEnd ? "period_end" : "now",
            Reason = CancellationReason.RequestedByCustomer,
            Feedback = feedback,
            Refund = kind switch
            {
                CancelKind.Now when choice.Refund > 0 => CancellationRefund.Prorated,
                CancelKind.Withdrawal => CancellationRefund.AllPayments,
                _ => CancellationRefund.None,
            },
        }, null, cancellationToken);
        return canceled.IsFailure ? canceled.Error : Result.Success();
    }

    /// <summary>Takes back a cancellation scheduled for the period's end (SUB-33).</summary>
    public async Task<Result> ResumeAsync(PortalSession session, Guid subscriptionId, CancellationToken cancellationToken)
    {
        Use(session);
        if (!portal.Value.IsEnabled(PortalFeatures.Resume))
        {
            return PortalCancelErrors.NotOffered;
        }

        var resumed = await resume.HandleAsync(subscriptionId, null, cancellationToken);
        return resumed.IsFailure ? resumed.Error : Result.Success();
    }

    /// <summary>The customer keeps the subscription with the retention coupon (PRT-8).</summary>
    public async Task<Result> AcceptOfferAsync(PortalSession session, Guid subscriptionId, string locale, CancellationToken cancellationToken)
    {
        if (await ChoicesAsync(session, subscriptionId, locale, cancellationToken) is not { RetentionOffer: not null } || portal.Value.RetentionCoupon is not { } coupon)
        {
            return PortalCancelErrors.NotOffered;
        }

        var kept = await update.HandleAsync(subscriptionId, new UpdateSubscriptionRequest { Coupon = coupon }, null, cancellationToken);
        return kept.IsFailure ? kept.Error : Result.Success();
    }

    /// <summary>
    /// Within 14 days of the subscription's start, unless the customer agreed at checkout that the service starts at
    /// once, which ends the right (CHK-12, PRT-10).
    /// </summary>
    private async Task<bool> MayWithdrawAsync(Subscription subscription, DateTimeOffset now, CancellationToken cancellationToken) =>
        now - subscription.CreatedAt < WithdrawalPeriod
        && !(subscription.CheckoutSessionId is { } sessionId
            && await context.ConsentRecords.AnyAsync(record => record.CheckoutSessionId == sessionId && record.Type == ConsentType.ImmediateStart, cancellationToken));

    /// <summary>The configured retention coupon, described, for a subscription without a discount that it could still be given to.</summary>
    private async Task<string?> RetentionOfferAsync(Subscription subscription, string locale, CancellationToken cancellationToken)
    {
        if (portal.Value.RetentionCoupon is not { } key || subscription.DiscountId is not null
            || await context.Coupons.AsNoTracking().SingleOrDefaultAsync(coupon => coupon.Key == key, cancellationToken) is not { } coupon
            || coupon.CanRedeem(clock.GetUtcNow()).IsFailure)
        {
            return null;
        }

        var culture = Formatting.CultureFor(locale);
        var exponent = Domain.Money.Currency.TryGet(subscription.Currency, out var currency) ? currency.Exponent : 2;
        return CouponDisplay.Describe(coupon, locale, amount => Formatting.Money(amount, subscription.Currency, exponent, culture), subscription.Interval, subscription.IntervalCount);
    }

    private void Use(PortalSession session) => caller.Use(new CustomerCaller(session.CustomerId, CustomerSessionKind.Portal, session.Id));
}
