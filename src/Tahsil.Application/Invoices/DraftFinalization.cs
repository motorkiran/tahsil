using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Domain.Invoices;

namespace Tahsil.Application.Invoices;

/// <summary>Finalizes drafts whose draft period ended (ENG-11), also when their subscription ended meanwhile (ENG-12).</summary>
public sealed class DraftFinalization(ITahsilDbContext context, IBillingLocks locks, InvoiceFinalizer finalizer, IWorkerSignals signals, BillingClock clock)
{
    /// <summary>Runs in the finalization worker's transaction, which holds the invoice's row lock.</summary>
    public async Task FinalizeDueAsync(Guid invoiceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await locks.LockInvoiceAsync(invoiceId, cancellationToken);
        if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is not { Status: InvoiceStatus.Draft, AutoAdvance: true, FinalizeAt: { } due } invoice || due > now)
        {
            return;
        }

        if (await finalizer.FinalizeAsync(invoice, cancellationToken) is { IsFailure: true } failed)
        {
            // The worker backs off and reports the draft as needing attention (ENG-6).
            throw new InvalidOperationException($"Invoice {invoice.Id} could not be finalized: {failed.Error.Code}: {failed.Error.Message}");
        }

        if (invoice is { Status: InvoiceStatus.Open, CollectionMethod: CollectionMethod.ChargeAutomatically, SubscriptionId: { } subscriptionId }
            && await context.Invoices.AnyAsync(
                other => other.SubscriptionId == subscriptionId && other.Id != invoice.Id && other.Status == InvoiceStatus.Open && other.CollectionMethod == CollectionMethod.ChargeAutomatically,
                cancellationToken))
        {
            // The subscription's older unpaid invoices are collected first; paying them brings this one's turn (DUN-9).
            invoice.WaitForOlderInvoices(clock.GetUtcNow());
        }

        await context.SaveChangesAsync(cancellationToken);
        if (invoice is { Status: InvoiceStatus.Open, CollectionMethod: CollectionMethod.ChargeAutomatically, NextPaymentAttemptAt: not null })
        {
            // Wakes the payment worker; one that looks before the worker's commit finds the invoice at its next poll.
            signals.Notify(Payments.WorkerNames.PaymentCollection);
        }
    }
}
