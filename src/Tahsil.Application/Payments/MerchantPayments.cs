using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Payments;

/// <summary>
/// Charges of saved cards without the customer (ENG-13): written ahead under the invoice's lock, charged outside any
/// transaction, and recorded by the outcome rules. An answer that never arrives is left to reconciliation.
/// </summary>
public sealed class MerchantPayments(
    ITahsilDbContext context,
    IBillingLocks locks,
    IIdempotency idempotency,
    IRequestContext request,
    ProviderRegistry registry,
    ProviderAccess providers,
    PaymentRequests requests,
    PaymentOutcomes outcomes,
    SavedCards cards,
    IOptions<PaymentOptions> options,
    BillingClock clock)
{
    /// <summary>Charges the given card for the invoice now, whatever its schedule (INV §5.3).</summary>
    public async Task<Result<Payment>> ChargeAsync(Guid invoiceId, Guid paymentMethodId, CancellationToken cancellationToken)
    {
        var written = await WriteAheadAsync(invoiceId, paymentMethodId, cancellationToken);
        return written.IsFailure ? written.Error : await ChargeWrittenAsync(written.Value.Payment, written.Value.Method, written.Value.Connection, cancellationToken);
    }

    /// <summary>
    /// The payment worker's attempt for a due invoice (ENG-13): the scheduled attempt is claimed, the card is the
    /// customer's default, and without a usable card the attempt is recorded as failed without a provider call.
    /// Returns <see langword="null"/> when the invoice is no longer due.
    /// </summary>
    public async Task<Payment?> CollectDueAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        Payment payment;
        PaymentMethod? method = null;
        ProviderConnection? connection = null;
        await using (var transaction = await context.BeginTransactionAsync(cancellationToken))
        {
            await locks.LockInvoiceAsync(invoiceId, cancellationToken);
            var invoice = await context.FindInvoiceAsync(invoiceId, cancellationToken);
            if (invoice is not { Status: InvoiceStatus.Open, CollectionMethod: CollectionMethod.ChargeAutomatically, NextPaymentAttemptAt: not null }
                || !invoice.NeedsPayment(invoice.AmountRemaining))
            {
                return null;
            }

            var now = clock.GetUtcNow();
            if (await PaymentRequests.HasPaymentInFlightAsync(context, invoice.Id, cancellationToken))
            {
                // The customer may be paying on a page right now; try again once that payment is settled.
                invoice.PostponePaymentAttempt(now + options.Value.ReconcileAfter, now);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            invoice.ClaimPaymentAttempt(now);
            var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
            if (customer.DefaultPaymentMethodId is { } defaultMethod)
            {
                method = await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(row => row.Id == defaultMethod, cancellationToken);
                connection = method is null ? null : await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == method.ConnectionId, cancellationToken);
            }

            if (method is null || connection is null || !Usable(method, connection, invoice))
            {
                payment = Payment.FailedBeforeCharge(
                    invoice.CustomerId, invoice.Id, invoice.AmountRemaining, invoice.Currency, PaymentOutcomes.Failure(FailureCode.PaymentMethodMissing), request.Caller.ToActor(), now);
                context.Payments.Add(payment);
                outcomes.RecordFailure(payment, invoice);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return payment;
            }

            payment = Payment.ForMerchant(
                invoice.CustomerId, invoice.Id, connection.Id, method.Id, invoice.AmountRemaining, invoice.Currency, now + options.Value.ReconcileAfter, request.Caller.ToActor(), now);
            context.Payments.Add(payment);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await ChargeWrittenAsync(payment, method, connection, cancellationToken);
    }

    /// <summary>Steps 2 and 3 of ENG-13. The charge and its record run to the end even if the caller goes away: an abandoned call is only reconciled.</summary>
    private async Task<Payment> ChargeWrittenAsync(Payment payment, PaymentMethod method, ProviderConnection connection, CancellationToken cancellationToken)
    {
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == payment.InvoiceId, cancellationToken);
        if (providers.For(connection) is not { } call)
        {
            return await outcomes.ApplyAsync(payment.Id, PaymentOutcome.Failure(FailureCode.ProviderUnavailable), CancellationToken.None);
        }

        var outcome = await call.Provider.ChargeSavedCardAsync(call.Context, new ChargeRequest
        {
            PaymentId = payment.Id,
            Reference = payment.Reference,
            Amount = PaymentRequests.AmountOf(payment.Amount, payment.Currency),
            SavedCard = cards.ReferenceOf(method),
            Customer = await requests.CustomerAsync(payment.CustomerId, null, cancellationToken),
            Description = invoice.Number,
        }, CancellationToken.None);
        return await outcomes.ApplyAsync(payment.Id, outcome, CancellationToken.None);
    }

    private async Task<Result<(Payment Payment, PaymentMethod Method, ProviderConnection Connection)>> WriteAheadAsync(
        Guid invoiceId, Guid paymentMethodId, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockInvoiceAsync(invoiceId, cancellationToken);
        if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is not { } invoice)
        {
            return InvoiceErrors.NotFound;
        }

        if (!invoice.NeedsPayment(invoice.AmountRemaining))
        {
            return InvoiceErrors.NothingToPay;
        }

        if (await PaymentRequests.HasPaymentInFlightAsync(context, invoice.Id, cancellationToken))
        {
            return InvoiceErrors.PaymentInProgress;
        }

        var method = await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(row => row.Id == paymentMethodId, cancellationToken);
        var connection = method is null ? null : await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == method.ConnectionId, cancellationToken);
        if (method is null || connection is null || !Usable(method, connection, invoice))
        {
            return InvoiceErrors.PaymentMethodUnusable;
        }

        var now = clock.GetUtcNow();
        var payment = Payment.ForMerchant(
            invoice.CustomerId, invoice.Id, connection.Id, method.Id, invoice.AmountRemaining, invoice.Currency, now + options.Value.ReconcileAfter, request.Caller.ToActor(), now);
        context.Payments.Add(payment);
        idempotency.RecordCreated(ResourceTypes.Payment, payment.Id);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return InvoiceErrors.PaymentInProgress;
        }

        await transaction.CommitAsync(cancellationToken);
        return (payment, method, connection);
    }

    /// <summary>
    /// INV-10: an active card of the invoice's customer, with consent, whose connection is enabled, fits the mode and
    /// takes the currency. It need not be the connection routed for the currency: cards stay with the connection that saved them.
    /// </summary>
    private bool Usable(PaymentMethod method, ProviderConnection connection, Invoice invoice) => CanCharge(method, connection, invoice.CustomerId, invoice.Currency);

    /// <summary>Whether the card can be charged without the customer for the customer's payments in the currency (INV-10).</summary>
    public bool CanCharge(PaymentMethod method, ProviderConnection connection, Guid customerId, string currency) =>
        method.IsChargeable
        && method.CustomerId == customerId
        && connection.IsEnabled
        && ProviderRegistry.AllowsInMode(registry.Mode, connection.Provider, connection.Environment)
        && registry.Find(connection.Provider) is { } provider
        && provider.Descriptor.Currencies.Contains(currency)
        && provider.CapabilitiesFor(connection.Settings).ChargesSavedCards;
}
