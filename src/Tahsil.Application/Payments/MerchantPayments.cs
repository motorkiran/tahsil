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
    public async Task<Result<Payment>> ChargeAsync(Guid invoiceId, Guid paymentMethodId, CancellationToken cancellationToken)
    {
        var written = await WriteAheadAsync(invoiceId, paymentMethodId, cancellationToken);
        if (written.IsFailure)
        {
            return written.Error;
        }

        var (payment, method, connection) = written.Value;
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId, cancellationToken);
        if (providers.For(connection) is not { } call)
        {
            return await outcomes.ApplyAsync(payment.Id, PaymentOutcome.Failure(FailureCode.ProviderUnavailable), CancellationToken.None);
        }

        // The charge and its record run to the end even if the caller goes away: an abandoned call is only reconciled.
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
    private bool Usable(PaymentMethod method, ProviderConnection connection, Invoice invoice) =>
        method.IsChargeable
        && method.CustomerId == invoice.CustomerId
        && connection.IsEnabled
        && ProviderRegistry.AllowsInMode(registry.Mode, connection.Provider, connection.Environment)
        && registry.Find(connection.Provider) is { } provider
        && provider.Descriptor.Currencies.Contains(invoice.Currency)
        && provider.CapabilitiesFor(connection.Settings).ChargesSavedCards;
}
