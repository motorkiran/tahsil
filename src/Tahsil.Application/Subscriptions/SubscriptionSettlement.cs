using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Subscriptions;

/// <summary>How a subscription follows the payment of its invoices (DOM §7.3).</summary>
public sealed class SubscriptionSettlement(ITahsilDbContext context, IBillingLocks locks, IAuditLog audit)
{
    private static readonly string[] StatusChanged = ["status"];

    /// <summary>
    /// An invoice of a subscription was paid in full, in the caller's transaction, which holds the invoice's lock: an
    /// incomplete subscription's first invoice (SUB-8), the first invoice after a trial (SUB-14), or the last open
    /// invoice of a past due subscription makes it active.
    /// </summary>
    public async Task InvoicePaidAsync(Invoice invoice, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invoice.SubscriptionId is not { } subscriptionId)
        {
            return;
        }

        await locks.LockSubscriptionAsync(subscriptionId, cancellationToken);
        var subscription = await context.Subscriptions.SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        var activates = subscription.Status switch
        {
            SubscriptionStatus.Incomplete => invoice.BillingReason == BillingReason.SubscriptionCreate,
            SubscriptionStatus.Trialing => subscription.TrialEnded,
            SubscriptionStatus.PastDue => !await context.Invoices.AnyAsync(
                other => other.SubscriptionId == subscriptionId && other.Id != invoice.Id && other.Status == InvoiceStatus.Open, cancellationToken),
            _ => false,
        };
        if (!activates)
        {
            return;
        }

        var previous = subscription.Status;
        subscription.Activate(now);
        audit.Record(AuditActions.SubscriptionUpdated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["changed"] = StatusChanged,
            ["status"] = SubscriptionMapper.Wire(subscription.Status),
            ["previousStatus"] = SubscriptionMapper.Wire(previous),
        }, customerId: subscription.CustomerId);
    }
}
