using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Subscriptions;

/// <summary>How a subscription follows the payment of its invoices (DOM §7.3).</summary>
public sealed class SubscriptionSettlement(ITahsilDbContext context, IBillingLocks locks, IAuditLog audit, IWorkerSignals signals)
{
    internal static readonly string[] StatusChanged = ["status"];

    /// <summary>
    /// An invoice of a subscription was paid in full, in the caller's transaction, which holds the invoice's lock: an
    /// incomplete subscription's first invoice (SUB-8), the first invoice after a trial (SUB-14), or the last open
    /// invoice of a past due subscription makes it active (DUN-10); the next unpaid invoice is collected now (DUN-9).
    /// </summary>
    public async Task InvoicePaidAsync(Invoice invoice, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invoice.SubscriptionId is not { } subscriptionId)
        {
            return;
        }

        await locks.LockSubscriptionAsync(subscriptionId, cancellationToken);
        var subscription = await context.Subscriptions.SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        await CollectNextAsync(invoice, subscriptionId, now, cancellationToken);
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
        RecordStatusChange(subscription, previous);
    }

    /// <summary>
    /// The first customer failure of an invoice of the subscription, or its due date passing, in the caller's
    /// transaction, which holds the invoice's lock (DUN-5, INV-13): an active subscription, or one whose trial just
    /// ended, is past due. An incomplete subscription stays incomplete (SUB-6).
    /// </summary>
    public async Task CustomerFailureAsync(Invoice invoice, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invoice.SubscriptionId is not { } subscriptionId)
        {
            return;
        }

        await locks.LockSubscriptionAsync(subscriptionId, cancellationToken);
        var subscription = await context.Subscriptions.SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        if (subscription.Status != SubscriptionStatus.Active && !(subscription.Status == SubscriptionStatus.Trialing && subscription.TrialEnded))
        {
            return;
        }

        var previous = subscription.Status;
        subscription.MarkPastDue(now);
        RecordStatusChange(subscription, previous);
        audit.Record(AuditActions.SubscriptionPastDue, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["invoiceId"] = invoice.Id,
            ["failureCode"] = failureCode,
        }, customerId: subscription.CustomerId);
    }

    /// <summary>The final action of dunning ends the subscription (DUN-6), in the caller's transaction, which holds the invoice's lock.</summary>
    public async Task CancelForNonPaymentAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await locks.LockSubscriptionAsync(subscriptionId, cancellationToken);
        var subscription = await context.Subscriptions.SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        if (subscription.Cancel(now, CancellationReason.PaymentFailed, null, null, now).IsFailure)
        {
            return;
        }

        audit.Record(AuditActions.SubscriptionCanceled, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["reason"] = SubscriptionMapper.Wire(CancellationReason.PaymentFailed),
            ["endedAt"] = subscription.EndedAt,
        }, customerId: subscription.CustomerId);
    }

    /// <summary>
    /// When the oldest unpaid invoice of a subscription is paid, the next one, held meanwhile, is tried now (DUN-9).
    /// Its lock is taken after the subscription's, unlike other paths; PostgreSQL resolves the rare deadlock.
    /// </summary>
    private async Task CollectNextAsync(Invoice paid, Guid subscriptionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = context.Invoices.Where(other => other.SubscriptionId == subscriptionId && other.Id != paid.Id && other.Status == InvoiceStatus.Open);
        if (await open.AnyAsync(other => other.FinalizedAt < paid.FinalizedAt, cancellationToken)
            || await open.Where(other => other.CollectionMethod == CollectionMethod.ChargeAutomatically && other.NextPaymentAttemptAt == null && other.DunningEndedAt == null)
                .OrderBy(other => other.FinalizedAt).Select(other => (Guid?)other.Id).FirstOrDefaultAsync(cancellationToken) is not { } nextId)
        {
            return;
        }

        await locks.LockInvoiceAsync(nextId, cancellationToken);
        if (await context.FindInvoiceAsync(nextId, cancellationToken) is { Status: InvoiceStatus.Open } next)
        {
            next.PostponePaymentAttempt(now, now);
            signals.Notify(Payments.WorkerNames.PaymentCollection);
        }
    }

    private void RecordStatusChange(Subscription subscription, SubscriptionStatus previous) =>
        audit.Record(AuditActions.SubscriptionUpdated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["changed"] = StatusChanged,
            ["status"] = SubscriptionMapper.Wire(subscription.Status),
            ["previousStatus"] = SubscriptionMapper.Wire(previous),
        }, customerId: subscription.CustomerId);
}
