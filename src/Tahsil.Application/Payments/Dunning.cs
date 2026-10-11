using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Subscriptions;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Payments;

internal enum DunningInvoiceAction
{
    /// <summary>The invoice stays payable on its hosted page.</summary>
    LeaveOpen,
    MarkUncollectible,
    Void,
}

public sealed class DunningOptions
{
    public const string SectionName = "Tahsil:Dunning";

    private static readonly TimeSpan[] DefaultRetrySchedule = [TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(5), TimeSpan.FromDays(7)];

    /// <summary>
    /// The retries, as offsets from the invoice's first customer failure (DUN-1); 1, 3, 5 and 7 days when not set.
    /// Starts empty: configuration binding would add to defaults instead of replacing them.
    /// </summary>
    public List<TimeSpan> RetrySchedule { get; set; } = [];

    internal IReadOnlyList<TimeSpan> Schedule => RetrySchedule.Count > 0 ? RetrySchedule : DefaultRetrySchedule;

    /// <summary>Whether the final action cancels the subscription; otherwise it stays past due for the operator (DUN-6).</summary>
    public bool CancelSubscription { get; set; } = true;

    /// <summary>What the final action does with the invoice: <c>leave_open</c> (the default), <c>mark_uncollectible</c> or <c>void</c>.</summary>
    public string InvoiceAction { get; set; } = "leave_open";

    internal DunningInvoiceAction Action => InvoiceAction switch
    {
        "mark_uncollectible" => DunningInvoiceAction.MarkUncollectible,
        "void" => DunningInvoiceAction.Void,
        _ => DunningInvoiceAction.LeaveOpen,
    };

    /// <summary>How long before the last retry the customer gets the last reminder (DUN §7).</summary>
    public TimeSpan FinalReminderBefore { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Whether every failed retry is emailed, not only the first failure (DUN §7).</summary>
    public bool EmailEveryRetry { get; set; }

    internal bool IsValid() =>
        InvoiceAction is "leave_open" or "mark_uncollectible" or "void"
        && Schedule.All(offset => offset > TimeSpan.Zero) && Schedule.SequenceEqual(Schedule.Order()) && FinalReminderBefore >= TimeSpan.Zero;
}

/// <summary>
/// What follows a failed automatic payment (DUN §2 to §6): only the customer's failures count against the customer.
/// Retryable declines are retried on the schedule, other customer failures wait for the customer, and the merchant's
/// failures are retried on a technical backoff and reported to operators without touching the customer.
/// </summary>
public sealed class Dunning(
    ITahsilDbContext context,
    IBillingLocks locks,
    IAuditLog audit,
    IWorkerSignals signals,
    Attention attention,
    InvoiceLinks links,
    InvoiceVoiding voiding,
    SubscriptionSettlement subscriptions,
    IOptions<DunningOptions> options)
{
    private static readonly Counter<long> MerchantFailures = TahsilDiagnostics.Meter.CreateCounter<long>(
        "tahsil.dunning.merchant_failures", unit: "{payment}", description: "Automatic payments that failed for the merchant's or provider's reasons, by connection and code.");

    private static readonly Counter<long> FinalActions = TahsilDiagnostics.Meter.CreateCounter<long>(
        "tahsil.dunning.canceled", unit: "{invoice}", description: "Invoices whose dunning ended unpaid with the final action.");

    private static readonly Counter<long> Recoveries = TahsilDiagnostics.Meter.CreateCounter<long>(
        "tahsil.dunning.recovered", unit: "{invoice}", description: "Invoices paid after a customer failure, by the number of automatic attempts.");

    private static readonly TimeSpan MerchantAttentionAfter = TimeSpan.FromHours(24);

    /// <summary>Counts an invoice paid after dunning started (DUN §10).</summary>
    public static void Recovered(Invoice invoice)
    {
        if (invoice is { Status: InvoiceStatus.Paid, DunningStartedAt: not null })
        {
            Recoveries.Add(1, new KeyValuePair<string, object?>("attempts", invoice.AttemptCount));
        }
    }

    /// <summary>
    /// An automatic payment of the invoice failed, in the caller's transaction, which holds the invoice's lock (DUN-2
    /// to DUN-5, DUN-7).
    /// </summary>
    public async Task PaymentFailedAsync(Payment payment, Invoice invoice, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (payment.FailureCategory == "merchant")
        {
            invoice.RecordMerchantFailure(now);
            MerchantFailures.Add(1, new KeyValuePair<string, object?>("connection", payment.ConnectionId), new KeyValuePair<string, object?>("failure_code", payment.FailureCode));
            if (payment.ConnectionId is { } connectionId && (payment.FailureCode == "merchant_not_permitted" || now - invoice.MerchantFailingSince >= MerchantAttentionAfter))
            {
                await attention.RaiseAsync(AttentionKind.MerchantFailures, AuditSubjects.Connection, connectionId, null, new Dictionary<string, string>
                {
                    ["failureCode"] = payment.FailureCode ?? string.Empty,
                    ["invoiceId"] = invoice.Id.ToString(),
                }, now, cancellationToken);
            }

            return;
        }

        var first = invoice.RecordCustomerFailure(options.Value.Schedule, retries: payment.FailureCategory == "retry", now);
        if (payment.FailureCategory == "authenticate")
        {
            audit.Record(AuditActions.InvoiceActionRequired, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), new Dictionary<string, object?>
            {
                ["number"] = invoice.Number,
                ["hostedUrl"] = links.UrlFor(invoice),
            }, customerId: invoice.CustomerId);
        }

        if (first)
        {
            await subscriptions.CustomerFailureAsync(invoice, payment.FailureCode, now, cancellationToken);
        }
    }

    /// <summary>
    /// A new card for a subscription, or a new default card for a customer (DUN-8): the oldest unpaid automatic invoice
    /// of each affected subscription that waits after a customer failure is tried now. Runs after the change commits.
    /// </summary>
    public async Task RetryWaitingAsync(Guid customerId, Guid? subscriptionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var waiting = context.Invoices.AsNoTracking().Where(invoice => invoice.CustomerId == customerId && invoice.Status == InvoiceStatus.Open
            && invoice.CollectionMethod == CollectionMethod.ChargeAutomatically && invoice.DunningStartedAt != null && invoice.DunningEndedAt == null);
        if (subscriptionId is { } only)
        {
            waiting = waiting.Where(invoice => invoice.SubscriptionId == only);
        }
        else
        {
            // Subscriptions with a card of their own do not use the customer's default.
            var ownCards = context.Subscriptions.Where(subscription => subscription.CustomerId == customerId && subscription.DefaultPaymentMethodId != null).Select(subscription => subscription.Id);
            waiting = waiting.Where(invoice => invoice.SubscriptionId == null || !ownCards.Contains(invoice.SubscriptionId.Value));
        }

        var oldest = (await waiting.Select(invoice => new { invoice.Id, invoice.SubscriptionId, invoice.FinalizedAt }).ToListAsync(cancellationToken))
            .GroupBy(invoice => invoice.SubscriptionId ?? invoice.Id)
            .Select(group => group.MinBy(invoice => invoice.FinalizedAt)!.Id)
            .ToList();
        var retried = false;
        foreach (var invoiceId in oldest)
        {
            await using var transaction = await context.BeginTransactionAsync(cancellationToken);
            await locks.LockInvoiceAsync(invoiceId, cancellationToken);
            if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is { } invoice && invoice.RetryNow(now))
            {
                await context.SaveChangesAsync(cancellationToken);
                retried = true;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (retried)
        {
            signals.Notify(WorkerNames.PaymentCollection);
        }
    }

    /// <summary>
    /// The final action, once, for an invoice still unpaid when its schedule ran out (DUN-6): the subscription is
    /// canceled for non-payment unless configured otherwise, and the invoice handled as configured. Runs in the dunning
    /// worker's transaction, which holds the invoice's lock.
    /// </summary>
    public async Task FinalActionAsync(Guid invoiceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is not
            { Status: InvoiceStatus.Open, DunningDeadlineAt: { } deadline, DunningEndedAt: null, NextPaymentAttemptAt: null } invoice
            || deadline > now || await PaymentRequests.HasPaymentInFlightAsync(context, invoiceId, cancellationToken))
        {
            return;
        }

        if (invoice.SubscriptionId is { } subscriptionId && options.Value.CancelSubscription)
        {
            await subscriptions.CancelForNonPaymentAsync(subscriptionId, now, cancellationToken);
        }

        switch (options.Value.Action)
        {
            case DunningInvoiceAction.Void when invoice.AmountPaid == 0:
                await voiding.VoidAsync(invoice, Actor.System, now, cancellationToken);
                break;
            case DunningInvoiceAction.Void or DunningInvoiceAction.MarkUncollectible:
                invoice.MarkUncollectible(now);
                audit.Record(AuditActions.InvoiceMarkedUncollectible, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), InvoiceFinalizer.Details(invoice), customerId: invoice.CustomerId);
                break;
        }

        invoice.EndDunning(now);
        FinalActions.Add(1, new KeyValuePair<string, object?>("invoice_action", options.Value.InvoiceAction));
        await context.SaveChangesAsync(cancellationToken);
    }
}
