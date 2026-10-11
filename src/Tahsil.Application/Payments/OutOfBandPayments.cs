using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Subscriptions;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;
using OutOfBandMethod = Tahsil.Contracts.OutOfBandMethod;
using OutOfBandPaymentRequest = Tahsil.Contracts.OutOfBandPaymentRequest;
using PaymentMethodKind = Tahsil.Domain.Payments.PaymentMethodKind;

namespace Tahsil.Application.Payments;

/// <summary>
/// Records money an operator received outside Tahsil, such as a bank transfer (INV-14, INV-15): in full or in part,
/// never more than is left to pay, under the invoice's lock, and audited with the actor.
/// </summary>
public sealed class OutOfBandPayments(
    ITahsilDbContext context, IBillingLocks locks, IAuditLog audit, IRequestContext request, SubscriptionSettlement subscriptions, BillingClock clock)
{
    public async Task<Result<Payment>> RecordAsync(Guid invoiceId, OutOfBandPaymentRequest received, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockInvoiceAsync(invoiceId, cancellationToken);
        if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is not { } invoice)
        {
            return InvoiceErrors.NotFound;
        }

        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Uncollectible) || invoice.AmountRemaining == 0)
        {
            return InvoiceErrors.NothingToPay;
        }

        var amount = received.Amount ?? invoice.AmountRemaining;
        if (!invoice.NeedsPayment(amount))
        {
            return InvoiceErrors.Overpayment;
        }

        // A card payment in flight could be paid on top of this; recording waits until it settles.
        if (await PaymentRequests.HasPaymentInFlightAsync(context, invoice.Id, cancellationToken))
        {
            return InvoiceErrors.PaymentInProgress;
        }

        var now = clock.GetUtcNow();
        var method = received.Method switch
        {
            OutOfBandMethod.Cash => PaymentMethodKind.Cash,
            OutOfBandMethod.Other => PaymentMethodKind.Other,
            _ => PaymentMethodKind.BankTransfer,
        };
        var recorded = Payment.RecordOutOfBand(
            invoice.CustomerId, invoice.Id, amount, invoice.Currency, method, new OutOfBandDetails(received.ReceivedAt ?? now, received.Reference, received.Note), request.Caller.ToActor(), now);
        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        var payment = recorded.Value;
        context.Payments.Add(payment);
        invoice.ApplyPayment(amount, now);
        audit.Record(AuditActions.PaymentSucceeded, AuditSubject.Of(AuditSubjects.Payment, payment.Id), new Dictionary<string, object?>
        {
            ["amount"] = payment.Amount,
            ["currency"] = payment.Currency,
            ["invoiceId"] = payment.InvoiceId,
            ["initiator"] = "operator",
            ["method"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(payment.Method.ToString()),
            ["connectionId"] = null,
        }, customerId: payment.CustomerId);
        if (invoice.Status == InvoiceStatus.Paid)
        {
            audit.Record(AuditActions.InvoicePaid, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), InvoiceFinalizer.Details(invoice), customerId: invoice.CustomerId);
            Dunning.Recovered(invoice);
            await subscriptions.InvoicePaidAsync(invoice, now, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return payment;
    }
}
