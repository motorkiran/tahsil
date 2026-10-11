using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Notifications;

/// <summary>
/// Reminders ahead of what is coming (ENG-20): a trial's end (SUB-16), a renewal (NTF-6) and a card expiring under a
/// subscription (CUS-15). Each runs in its worker's transaction, which holds the subject's lock, and is recorded on the
/// subject, so its event and email happen once per occurrence.
/// </summary>
public sealed class Reminders(ITahsilDbContext context, IAuditLog audit, IOutbox outbox, ChargeableCards cards, IOptions<NotificationOptions> options, BillingClock clock)
{
    public async Task TrialEndingAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        var now = clock.GetUtcNow();
        if (subscription is not { Status: SubscriptionStatus.Trialing, TrialEnded: false, TrialEnd: { } trialEnd } || subscription.TrialRemindedFor == trialEnd || trialEnd <= now)
        {
            return;
        }

        subscription.RecordTrialReminder(now);
        audit.Record(AuditActions.SubscriptionTrialWillEnd, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["trialEnd"] = trialEnd,
        }, customerId: subscription.CustomerId);

        // A trial that charges a saved card at its end must say so, and the notice cannot be turned off (SUB-16, CHK-13).
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        var charged = subscription.CollectionMethod == Domain.Invoices.CollectionMethod.ChargeAutomatically
            && await cards.ForInvoiceAsync(customer, subscription.DefaultPaymentMethodId, subscription.Currency, cancellationToken) is not null;
        CustomerEmails.Enqueue(outbox, EmailTypes.TrialEnding, subscription.Id, $"{subscription.Id}:{trialEnd.ToUnixTimeSeconds()}", new Dictionary<string, string>
        {
            ["mandatory"] = charged ? "true" : "false",
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpcomingRenewalAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        var now = clock.GetUtcNow();
        if (subscription.Status is not (SubscriptionStatus.Active or SubscriptionStatus.PastDue) || subscription.RenewalRemindedFor == subscription.CurrentPeriodEnd
            || subscription.CurrentPeriodEnd <= now || subscription.CancelAt <= subscription.CurrentPeriodEnd)
        {
            return;
        }

        subscription.RecordRenewalReminder(now);
        audit.Record(AuditActions.SubscriptionWillRenew, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["renewsAt"] = subscription.CurrentPeriodEnd,
        }, customerId: subscription.CustomerId);
        CustomerEmails.Enqueue(outbox, EmailTypes.UpcomingRenewal, subscription.Id, $"{subscription.Id}:{subscription.CurrentPeriodEnd.ToUnixTimeSeconds()}");
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task CardExpiringAsync(Guid paymentMethodId, CancellationToken cancellationToken)
    {
        var card = await context.PaymentMethods.SingleAsync(row => row.Id == paymentMethodId, cancellationToken);
        var now = clock.GetUtcNow();
        if (card is not { Status: PaymentMethodStatus.Active, ExpiryRemindedAt: null, ExpiresAt: { } expiresAt } || expiresAt <= now)
        {
            return;
        }

        var subscriptions = await SubscriptionsUsingAsync(card, cancellationToken);
        card.RecordExpiryReminder(now);
        if (subscriptions.Count > 0)
        {
            audit.Record(AuditActions.PaymentMethodWillExpire, AuditSubject.Of(AuditSubjects.PaymentMethod, card.Id), new Dictionary<string, object?>
            {
                ["expMonth"] = card.ExpMonth,
                ["expYear"] = card.ExpYear,
                ["subscriptionIds"] = subscriptions,
            }, customerId: card.CustomerId);
            CustomerEmails.Enqueue(outbox, EmailTypes.CardExpiring, card.Id, card.Id.ToString());
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The running subscriptions a card pays: its own, or those using the customer's default when it is that.</summary>
    internal async Task<List<Guid>> SubscriptionsUsingAsync(PaymentMethod card, CancellationToken cancellationToken)
    {
        var isDefault = await context.Customers.AnyAsync(customer => customer.Id == card.CustomerId && customer.DefaultPaymentMethodId == card.Id, cancellationToken);
        return await context.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.CustomerId == card.CustomerId
                && (subscription.Status == SubscriptionStatus.Trialing || subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.PastDue)
                && (subscription.DefaultPaymentMethodId == card.Id || (subscription.DefaultPaymentMethodId == null && isDefault)))
            .OrderBy(subscription => subscription.Id)
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The notice ahead of a renewal for subscriptions of an interval (NTF-6).</summary>
    public int RenewalNoticeDays(Domain.Catalog.PriceInterval interval) => options.Value.RenewalNoticeDays(interval.ToString());
}
