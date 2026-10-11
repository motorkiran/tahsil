using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;
using PaymentStatus = Tahsil.Domain.Payments.PaymentStatus;
using ProviderStatus = Tahsil.Providers.Abstractions.PaymentStatus;

namespace Tahsil.Application.Payments;

/// <summary>
/// Applies what a provider said about a payment (PAY-12 to PAY-15), from any path: the customer's return, a charge,
/// a notification or reconciliation. It locks the payment and its invoice, so concurrent paths apply an outcome
/// once, and repeating an outcome changes nothing.
/// </summary>
public sealed class PaymentOutcomes(
    ITahsilDbContext context,
    IBillingLocks locks,
    IAuditLog audit,
    IOutbox outbox,
    Attention attention,
    SavedCards cards,
    Checkout.CheckoutCompletion checkout,
    Checkout.CheckoutProtection protection,
    BillingClock clock)
{
    private static readonly Counter<long> Payments = TahsilDiagnostics.Meter.CreateCounter<long>(
        "tahsil.payments", unit: "{payment}", description: "Payments that reached a final status, by connection, initiator, status and failure code.");

    public async Task<Payment> ApplyAsync(Guid paymentId, PaymentOutcome outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockPaymentAsync(paymentId, cancellationToken);
        var payment = await context.Payments.SingleAsync(row => row.Id == paymentId, cancellationToken);
        Invoice? invoice = null;
        if (payment.InvoiceId is { } invoiceId)
        {
            await locks.LockInvoiceAsync(invoiceId, cancellationToken);
            invoice = await context.FindInvoiceAsync(invoiceId, cancellationToken);
        }

        var now = clock.GetUtcNow();
        switch (outcome.Status)
        {
            case ProviderStatus.Succeeded:
                await SucceedAsync(payment, invoice, outcome, now, cancellationToken);
                break;
            case ProviderStatus.Failed:
                await FailAsync(payment, invoice, outcome, Failure(outcome.FailureCode ?? FailureCode.ProviderError, outcome), now, cancellationToken);
                break;
            case ProviderStatus.RequiresAction when payment.Initiator == PaymentInitiator.Merchant:
                // Nobody is there to authenticate an off-session charge (PAY-10).
                await FailAsync(payment, invoice, outcome, Failure(FailureCode.AuthenticationRequired, outcome), now, cancellationToken);
                break;
            case ProviderStatus.RequiresAction when payment.Status == PaymentStatus.Processing:
                payment.RequireAction(outcome.ProviderData.Count == 0 ? payment.ProviderData : outcome.ProviderData, now);
                break;
        }

        // Pending and unknown outcomes leave the payment to reconciliation (ENG-15).
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return payment;
    }

    private async Task SucceedAsync(Payment payment, Invoice? invoice, PaymentOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (payment.Status == PaymentStatus.Succeeded)
        {
            // PAY-13: the same result again changes nothing.
            return;
        }

        var late = !payment.IsInFlight;
        payment.Succeed(outcome.ProviderPaymentId, outcome.ProviderData, ThreeDSecureOf(outcome), now);
        Count(payment);
        audit.Record(AuditActions.PaymentSucceeded, AuditSubject.Of(AuditSubjects.Payment, payment.Id), Details(payment, late), customerId: payment.CustomerId);

        if (outcome.SavedCard is { } card && payment.SaveCard
            && await cards.SaveAsync(payment, card, now, cancellationToken) is { } method)
        {
            payment.AttachPaymentMethod(method.Id, now);
        }

        if (outcome.ChargedAmount is { } charged && (charged.MinorUnits != payment.Amount || charged.Currency != payment.Currency))
        {
            // PAY-15: the provider charged something else; an operator decides what to do with it.
            await attention.RaiseAsync(AttentionKind.AmountMismatch, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
            {
                ["expected"] = $"{payment.Amount} {payment.Currency}",
                ["charged"] = $"{charged.MinorUnits} {charged.Currency}",
            }, now, cancellationToken);
            return;
        }

        if (payment.CheckoutSessionId is not null)
        {
            await checkout.CompleteAsync(payment, now, cancellationToken);
            return;
        }

        if (payment.Amount == 0 || invoice is null)
        {
            return;
        }

        if (!invoice.NeedsPayment(payment.Amount))
        {
            // PAY-14: the money arrived after the invoice stopped needing it; it goes back to the customer once this commits.
            await attention.RaiseAsync(AttentionKind.LateSuccess, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
            {
                ["invoiceId"] = invoice.Id.ToString(),
                ["invoiceStatus"] = invoice.Status.ToString(),
            }, now, cancellationToken);
            outbox.Enqueue(new RefundLateSuccess(payment.Id));
            return;
        }

        invoice.ApplyPayment(payment.Amount, now);
        if (invoice.Status == InvoiceStatus.Paid)
        {
            audit.Record(AuditActions.InvoicePaid, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), InvoiceFinalizer.Details(invoice), customerId: invoice.CustomerId);
        }
    }

    private async Task FailAsync(Payment payment, Invoice? invoice, PaymentOutcome outcome, PaymentFailure failure, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (payment.Status == PaymentStatus.Succeeded)
        {
            // PAY-13: a failure reported for a payment that succeeded contradicts the provider's earlier answer.
            await attention.RaiseAsync(AttentionKind.ConflictingOutcome, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
            {
                ["reported"] = failure.Code,
            }, now, cancellationToken);
            return;
        }

        if (!payment.IsInFlight)
        {
            return;
        }

        payment.Fail(failure, outcome.ProviderPaymentId, ThreeDSecureOf(outcome), now);
        RecordFailure(payment, invoice);
        await protection.WatchDeclinesAsync(payment, now, cancellationToken);
    }

    /// <summary>Counts and announces a failed payment: <c>payment.failed</c>, and <c>invoice.payment_failed</c> for automatic charges.</summary>
    internal void RecordFailure(Payment payment, Invoice? invoice)
    {
        Count(payment);
        audit.Record(AuditActions.PaymentFailed, AuditSubject.Of(AuditSubjects.Payment, payment.Id), Details(payment, late: false), customerId: payment.CustomerId);
        if (invoice is not null && payment.Initiator == PaymentInitiator.Merchant)
        {
            audit.Record(AuditActions.InvoicePaymentFailed, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), new Dictionary<string, object?>
            {
                ["number"] = invoice.Number,
                ["amountDue"] = invoice.AmountDue,
                ["currency"] = invoice.Currency,
                ["failureCode"] = payment.FailureCode,
                ["failureCategory"] = payment.FailureCategory,
                ["attempt"] = invoice.AttemptCount,
                ["nextAttemptAt"] = invoice.NextPaymentAttemptAt,
            }, customerId: invoice.CustomerId);
        }
    }

    /// <summary>The customer's time ran out and the provider has no success for the payment (ENG-18).</summary>
    public async Task<Payment> CancelAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockPaymentAsync(paymentId, cancellationToken);
        var payment = await context.Payments.SingleAsync(row => row.Id == paymentId, cancellationToken);
        if (payment.Status == PaymentStatus.RequiresAction)
        {
            payment.Cancel(clock.GetUtcNow());
            Count(payment);
            audit.Record(AuditActions.PaymentCanceled, AuditSubject.Of(AuditSubjects.Payment, payment.Id), Details(payment, late: false), customerId: payment.CustomerId);
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return payment;
    }

    internal static PaymentFailure Failure(FailureCode code, PaymentOutcome? outcome = null) =>
        new(FailureMessages.Name(code), FailureMessages.Category(code), FailureMessages.English(code), outcome?.ProviderFailureCode, outcome?.ProviderFailureMessage);

    private static bool? ThreeDSecureOf(PaymentOutcome outcome) => outcome.ThreeDSecure switch
    {
        ThreeDSecureResult.Authenticated => true,
        ThreeDSecureResult.Failed => false,
        _ => null,
    };

    private static Dictionary<string, object?> Details(Payment payment, bool late) => new()
    {
        ["amount"] = payment.Amount,
        ["currency"] = payment.Currency,
        ["invoiceId"] = payment.InvoiceId,
        ["initiator"] = payment.Initiator.ToString().ToLowerInvariant(),
        ["method"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(payment.Method.ToString()),
        ["connectionId"] = payment.ConnectionId,
        ["failureCode"] = payment.FailureCode,
        ["failureCategory"] = payment.FailureCategory,
        ["lateSuccess"] = late ? true : null,
    };

    private static void Count(Payment payment) => Payments.Add(
        1,
        new KeyValuePair<string, object?>("connection", payment.ConnectionId),
        new KeyValuePair<string, object?>("initiator", payment.Initiator.ToString().ToLowerInvariant()),
        new KeyValuePair<string, object?>("status", payment.Status.ToString().ToLowerInvariant()),
        new KeyValuePair<string, object?>("failure_code", payment.FailureCode));
}

/// <summary>Raises attention items for operators, one open item per kind and subject.</summary>
public sealed class Attention(ITahsilDbContext context, IAuditLog audit)
{
    public async Task RaiseAsync(
        AttentionKind kind, string subjectType, Guid subjectId, Guid? customerId, IReadOnlyDictionary<string, string> details, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.AttentionItems.Local.Any(item => item.Kind == kind && item.SubjectId == subjectId && item.IsOpen)
            || await context.AttentionItems.AnyAsync(item => item.Kind == kind && item.SubjectId == subjectId && item.ResolvedAt == null, cancellationToken))
        {
            return;
        }

        var item = AttentionItem.Raise(kind, subjectType, subjectId, customerId, details, now);
        context.AttentionItems.Add(item);
        audit.Record(AuditActions.AttentionRaised, new AuditSubject(subjectType, subjectId.ToString()), new Dictionary<string, object?>
        {
            ["kind"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString()),
            ["attentionItemId"] = item.Id,
        }, customerId: customerId);
    }
}
