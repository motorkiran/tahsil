using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Checkout;

/// <summary>
/// Completes a checkout session when its payment succeeds (CHK-23), in the transaction that applies the outcome, so
/// whichever path brings the success (return, notification, reconciliation) completes it once: a paid invoice of the
/// quote's lines for <c>payment</c>, the saved card for <c>setup</c>, then <c>checkout.completed</c>.
/// </summary>
public sealed class CheckoutCompletion(ITahsilDbContext context, IBillingLocks locks, InvoiceFinalizer finalizer, IAuditLog audit, IOutbox outbox, Attention attention)
{
    /// <summary>Called with the payment and its lock held, after the payment succeeded and its card was saved.</summary>
    public async Task CompleteAsync(Payment payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessionId = payment.CheckoutSessionId ?? throw new ArgumentException("The payment is not a checkout's.", nameof(payment));
        await locks.LockCheckoutSessionAsync(sessionId, cancellationToken);
        var session = await context.CheckoutSessions.SingleAsync(candidate => candidate.Id == sessionId, cancellationToken);
        if (session.Status != CheckoutStatus.Open)
        {
            if (session.PaymentId != payment.Id && payment.Amount > 0)
            {
                // PAY-14: the session expired or another attempt completed it; nothing needs this money.
                await attention.RaiseAsync(AttentionKind.LateSuccess, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
                {
                    ["checkoutSessionId"] = session.Id.ToString(),
                    ["sessionStatus"] = session.Status.ToString(),
                }, now, cancellationToken);
                outbox.Enqueue(new RefundLateSuccess(payment.Id));
            }

            return;
        }

        if (session.Mode == CheckoutMode.Setup && payment.PaymentMethodId is null)
        {
            // CHK-24: the provider saved no card; the session stays open for another try.
            return;
        }

        Guid? invoiceId = null;
        if (session.Mode == CheckoutMode.Payment)
        {
            invoiceId = await PaidInvoiceAsync(session, payment, now, cancellationToken);
        }

        session.Complete(payment.Id, invoiceId, payment.PaymentMethodId, now);
        audit.Record(
            AuditActions.CheckoutCompleted,
            AuditSubject.Of(AuditSubjects.CheckoutSession, session.Id),
            new Dictionary<string, object?>
            {
                ["mode"] = session.Mode.ToString().ToLowerInvariant(),
                ["clientReferenceId"] = session.ClientReferenceId,
                ["subscriptionId"] = null,
                ["invoiceId"] = invoiceId,
                ["paymentMethodId"] = payment.PaymentMethodId,
            },
            customerId: session.CustomerId);
    }

    /// <summary>The invoice of the quote, finalized and paid by the payment (<c>billing_reason = checkout</c>).</summary>
    private async Task<Guid> PaidInvoiceAsync(CheckoutSession session, Payment payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == payment.CustomerId, cancellationToken);
        var invoice = Invoice.CreateDraft(payment.CustomerId, session.Currency, BillingReason.Checkout, CollectionMethod.ChargeAutomatically, null, null, false, session.Metadata, now).Value;
        invoice.AddLines(session.Quote.Drafts(), customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        if (await finalizer.FinalizeAsync(invoice, cancellationToken, applyBalance: false) is { IsFailure: true } failed)
        {
            throw new InvalidOperationException($"The invoice of checkout session {session.Id} cannot be finalized: {failed.Error.Message}");
        }

        payment.AttachInvoice(invoice.Id, now);
        if (invoice.ApplyPayment(payment.Amount, now).IsFailure || invoice.Status != InvoiceStatus.Paid)
        {
            // The quote changed between the attempt and its success, which the page prevents; an operator settles the difference.
            await attention.RaiseAsync(AttentionKind.AmountMismatch, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
            {
                ["expected"] = $"{invoice.AmountDue.ToString(CultureInfo.InvariantCulture)} {invoice.Currency}",
                ["charged"] = $"{payment.Amount.ToString(CultureInfo.InvariantCulture)} {payment.Currency}",
            }, now, cancellationToken);
            return invoice.Id;
        }

        audit.Record(AuditActions.InvoicePaid, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), InvoiceFinalizer.Details(invoice), customerId: invoice.CustomerId);
        return invoice.Id;
    }
}
