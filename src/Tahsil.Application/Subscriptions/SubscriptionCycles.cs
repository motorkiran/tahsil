using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Subscriptions;

/// <summary>
/// The subscription worker's work on a due subscription (ENG-8): everything due at or before now, oldest first, in the
/// order of SUB-12 (incomplete expiry, trial end, cancellation, renewal), a period at a time when the worker is behind
/// (ENG-9). It runs in the worker's transaction, which holds the subscription's row lock.
/// </summary>
public sealed class SubscriptionCycles(
    ITahsilDbContext context,
    IBillingLocks locks,
    IAuditLog audit,
    SubscriptionInvoices invoices,
    SubscriptionLines lines,
    Payments.ChargeableCards cards,
    InvoiceVoiding voiding,
    IOptions<BillingOptions> billing)
{
    private static readonly string[] ItemsChanged = ["items"];
    private static readonly string[] ScheduledChangeDropped = ["scheduledChange"];

    public async Task ProcessAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.Include(row => row.Items).SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        while (subscription.NextCycleAt <= now)
        {
            if (!await StepAsync(subscription, customer, now, cancellationToken))
            {
                // Due with nothing to do would keep the worker busy forever; failing backs off and reports it (ENG-6).
                throw new InvalidOperationException($"Subscription {subscription.Id} is due at {subscription.NextCycleAt:O} with nothing to apply.");
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> StepAsync(Subscription subscription, Customer customer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (subscription.Status == SubscriptionStatus.Incomplete)
        {
            return await ExpireAsync(subscription, now, cancellationToken);
        }

        // A trial canceled at or before its end ends with the cancellation, without a first paid period.
        if (subscription is { Status: SubscriptionStatus.Trialing, TrialEnded: false, TrialEnd: { } trialEnd } && trialEnd <= now && !(subscription.CancelAt <= trialEnd))
        {
            return await EndTrialAsync(subscription, customer, trialEnd, now, cancellationToken);
        }

        if (subscription.CancelAt is { } cancelAt && cancelAt <= now)
        {
            return await CancelAsync(subscription, customer, cancelAt, subscription.CancellationReason ?? CancellationReason.RequestedByMerchant, now, cancellationToken);
        }

        if (subscription.ScheduledChange is { } change && change.EffectiveAt <= now)
        {
            await ApplyScheduledChangeAsync(subscription, customer, change, now, cancellationToken);
            return true;
        }

        if (subscription.CurrentPeriodEnd <= now)
        {
            var renewed = subscription.Renew(billing.Value.Zone, now);
            if (renewed.IsFailure)
            {
                return false;
            }

            await StartPeriodAsync(subscription, customer, renewed.Value, now, cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>The first invoice was not paid in time (SUB-8): it is voided and the subscription ends.</summary>
    private async Task<bool> ExpireAsync(Subscription subscription, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (subscription.LatestInvoiceId is { } invoiceId)
        {
            // The subscription is locked first here, unlike payment outcomes; PostgreSQL resolves the rare deadlock by
            // failing one side, which is retried.
            await locks.LockInvoiceAsync(invoiceId, cancellationToken);
            if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is { Status: InvoiceStatus.Open } invoice)
            {
                var voided = await voiding.VoidAsync(invoice, Actor.System, now, cancellationToken);
                if (voided.IsFailure)
                {
                    return false;
                }
            }
        }

        if (subscription.ExpireIncomplete(now).IsFailure)
        {
            return false;
        }

        RecordStatusChange(subscription, SubscriptionStatus.Incomplete);
        return true;
    }

    /// <summary>
    /// The trial reached its end (SUB-14, SUB-15): without a card to charge it is canceled, unless it was agreed to
    /// invoice anyway; otherwise the first paid period starts and is invoiced like a renewal.
    /// </summary>
    private async Task<bool> EndTrialAsync(Subscription subscription, Customer customer, DateTimeOffset trialEnd, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (subscription is { CollectionMethod: CollectionMethod.ChargeAutomatically, TrialEndBehavior: TrialEndBehavior.Cancel }
            && await cards.ForInvoiceAsync(customer, subscription.DefaultPaymentMethodId, subscription.Currency, cancellationToken) is null)
        {
            return await CancelAsync(subscription, customer, trialEnd, CancellationReason.TrialWithoutPaymentMethod, now, cancellationToken);
        }

        var period = subscription.EndTrial(billing.Value.Zone, now);
        if (period.IsFailure)
        {
            return false;
        }

        await StartPeriodAsync(subscription, customer, period.Value, now, cancellationToken);
        return true;
    }

    private async Task StartPeriodAsync(Subscription subscription, Customer customer, BillingPeriod period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var invoice = await invoices.CreateCycleInvoiceAsync(subscription, customer, period, now, cancellationToken);
        audit.Record(AuditActions.SubscriptionRenewed, Subject(subscription), new Dictionary<string, object?>
        {
            ["periodStart"] = period.Start,
            ["periodEnd"] = period.End,
            ["invoiceId"] = invoice?.Id,
        }, customerId: subscription.CustomerId);
    }

    /// <summary>
    /// The scheduled change takes effect at its renewal, before the renewal invoice (SUB-12, SUB-19). Prices archived
    /// meanwhile no longer qualify; the change is dropped then, and the event says so. The usage of metered items that
    /// leave waits for the renewal invoice as pending items (SUB-25).
    /// </summary>
    private async Task ApplyScheduledChangeAsync(Subscription subscription, Customer customer, ScheduledChange change, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var items = await lines.PricedAsync(change.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken);
        var leaving = subscription.Items.Where(item => items.All(kept => kept.Price.Id != item.PriceId)).ToList();
        var applied = subscription.CanReplaceItems(items)
            && await invoices.AddPendingUsageAsync(subscription, customer, leaving, change.EffectiveAt, now, cancellationToken)
            // As of the renewal it was scheduled for, whenever the worker gets to it: items that join are metered from then.
            && subscription.ReplaceItems(items, billing.Value.Zone, change.EffectiveAt).IsSuccess;
        if (!applied)
        {
            subscription.ClearScheduledChange(now);
        }

        audit.Record(AuditActions.SubscriptionUpdated, Subject(subscription), new Dictionary<string, object?>
        {
            ["changed"] = applied ? ItemsChanged : ScheduledChangeDropped,
            ["status"] = SubscriptionMapper.Wire(subscription.Status),
            ["previousStatus"] = null,
        }, customerId: subscription.CustomerId);
    }

    /// <summary>The subscription ends; its metered usage until then goes on a final invoice, if there is any (SUB-12, SUB-29).</summary>
    private async Task<bool> CancelAsync(
        Subscription subscription, Customer customer, DateTimeOffset endedAt, CancellationReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (subscription.Cancel(endedAt, reason, null, null, now).IsFailure)
        {
            return false;
        }

        if (!await invoices.CreateFinalUsageInvoiceAsync(subscription, customer, endedAt, now, cancellationToken))
        {
            return false;
        }

        audit.Record(AuditActions.SubscriptionCanceled, Subject(subscription), new Dictionary<string, object?>
        {
            ["reason"] = SubscriptionMapper.Wire(subscription.CancellationReason!.Value),
            ["endedAt"] = subscription.EndedAt,
        }, customerId: subscription.CustomerId);
        return true;
    }

    private void RecordStatusChange(Subscription subscription, SubscriptionStatus previous) =>
        audit.Record(AuditActions.SubscriptionUpdated, Subject(subscription), new Dictionary<string, object?>
        {
            ["changed"] = SubscriptionSettlement.StatusChanged,
            ["status"] = SubscriptionMapper.Wire(subscription.Status),
            ["previousStatus"] = SubscriptionMapper.Wire(previous),
        }, customerId: subscription.CustomerId);

    private static AuditSubject Subject(Subscription subscription) => AuditSubject.Of(AuditSubjects.Subscription, subscription.Id);
}
