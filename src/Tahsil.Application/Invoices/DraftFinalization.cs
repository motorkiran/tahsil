using Tahsil.Application.Common;
using Tahsil.Domain.Invoices;

namespace Tahsil.Application.Invoices;

/// <summary>Finalizes drafts whose draft period ended (ENG-11), also when their subscription ended meanwhile (ENG-12).</summary>
public sealed class DraftFinalization(ITahsilDbContext context, IBillingLocks locks, InvoiceFinalizer finalizer, IWorkerSignals signals)
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

        await context.SaveChangesAsync(cancellationToken);
        if (invoice is { Status: InvoiceStatus.Open, CollectionMethod: CollectionMethod.ChargeAutomatically })
        {
            // Wakes the payment worker; one that looks before the worker's commit finds the invoice at its next poll.
            signals.Notify(Payments.WorkerNames.PaymentCollection);
        }
    }
}
