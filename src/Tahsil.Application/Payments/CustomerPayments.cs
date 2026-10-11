using System.Net;
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
using PaymentPurpose = Tahsil.Domain.Payments.PaymentPurpose;

namespace Tahsil.Application.Payments;

/// <summary>The customer's consent to future charges of the card they are about to enter, as their page showed it (CUS-11).</summary>
public sealed record CardConsent(string TextShown, IPAddress? IpAddress, string? UserAgent);

/// <summary>A customer-present payment of an invoice: where to send the customer afterwards, and whether to save the card.</summary>
public sealed record StartInvoicePayment(Guid InvoiceId, Uri ReturnUrl, Uri CancelUrl, bool SaveCard, CardConsent? Consent, string? IpAddress);

public sealed record StartedPayment(Payment Payment, Presentation Presentation, DateTimeOffset? ExpiresAt);

/// <summary>
/// Payments with the customer present (PAY-8): written ahead, started at the provider, and completed from the
/// customer's return or a notification through the same idempotent step. Pages call these from M3 on.
/// </summary>
public sealed class CustomerPayments(
    ITahsilDbContext context,
    IBillingLocks locks,
    IRequestContext request,
    ProviderAccess providers,
    PaymentRequests requests,
    PaymentOutcomes outcomes,
    SavedCards cards,
    IOptions<PaymentOptions> options,
    BillingClock clock)
{
    public async Task<Result<StartedPayment>> StartAsync(StartInvoicePayment start, CancellationToken cancellationToken)
    {
        var written = await WriteAheadAsync(start, cancellationToken);
        if (written.IsFailure)
        {
            return written.Error;
        }

        var (payment, connection) = written.Value;
        return await StartWrittenAsync(payment, connection, start.ReturnUrl, start.CancelUrl, start.IpAddress, cancellationToken);
    }

    /// <summary>
    /// Step 2 of PAY-8 for a payment written ahead, of an invoice or a checkout: the provider's <em>start</em>, and the
    /// payment waits for the customer.
    /// </summary>
    public async Task<Result<StartedPayment>> StartWrittenAsync(
        Payment payment, ProviderConnection connection, Uri returnUrl, Uri cancelUrl, string? ipAddress, CancellationToken cancellationToken)
    {
        if (providers.For(connection) is not { } call)
        {
            await outcomes.ApplyAsync(payment.Id, PaymentOutcome.Failure(FailureCode.ProviderUnavailable), CancellationToken.None);
            return Error.Unavailable("payment.provider_unavailable", "The provider of the currency's connection cannot take payments now.");
        }

        var started = await call.Provider.StartAsync(call.Context, new StartRequest
        {
            PaymentId = payment.Id,
            Reference = payment.Reference,
            Amount = PaymentRequests.AmountOf(payment.Amount, payment.Currency),
            Purpose = payment.Purpose == PaymentPurpose.Setup ? Providers.Abstractions.PaymentPurpose.Setup : Providers.Abstractions.PaymentPurpose.Charge,
            SaveCard = payment.SaveCard,
            ProviderCustomer = await requests.ProviderCustomerAsync(payment.CustomerId, connection.Id, cancellationToken),
            Customer = await requests.CustomerAsync(payment.CustomerId, ipAddress, cancellationToken),
            ReturnUrl = returnUrl,
            CancelUrl = cancelUrl,
        }, CancellationToken.None);

        if (started.Failure is { } refused)
        {
            await outcomes.ApplyAsync(payment.Id, refused, CancellationToken.None);
            return Error.Unavailable("payment.start_failed", "The provider could not start the payment; nothing was charged. Please try again later.");
        }

        var presentation = started.Presentation ?? throw new InvalidOperationException($"The {connection.Provider} adapter started a payment without a presentation.");
        await using (var transaction = await context.BeginTransactionAsync(CancellationToken.None))
        {
            await locks.LockPaymentAsync(payment.Id, CancellationToken.None);
            var current = await context.Payments.SingleAsync(row => row.Id == payment.Id, CancellationToken.None);
            if (current.Status == Domain.Payments.PaymentStatus.Processing)
            {
                current.RequireAction(started.ProviderData, clock.GetUtcNow());
            }

            if (started.ProviderCustomer is { } providerCustomer)
            {
                await cards.RememberProviderCustomerAsync(current.CustomerId, connection.Id, providerCustomer, clock.GetUtcNow(), CancellationToken.None);
            }

            await context.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            payment = current;
        }

        return new StartedPayment(payment, presentation, started.ExpiresAt ?? payment.ExpiresAt);
    }

    /// <summary>Learns from the provider how the payment ended and applies it; a payment already over is returned as it is.</summary>
    public async Task<Result<Payment>> CompleteAsync(Guid paymentId, IReadOnlyDictionary<string, string> returnValues, CancellationToken cancellationToken)
    {
        if (await context.Payments.AsNoTracking().SingleOrDefaultAsync(row => row.Id == paymentId, cancellationToken) is not { } payment)
        {
            return PaymentErrors.NotFound;
        }

        if (!payment.IsInFlight || payment.ConnectionId is not { } connectionId)
        {
            return payment;
        }

        var connection = await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == connectionId, cancellationToken);
        if (providers.For(connection) is not { } call)
        {
            return payment;
        }

        var outcome = await call.Provider.CompleteAsync(call.Context, new CompleteRequest
        {
            PaymentId = payment.Id,
            Reference = payment.Reference,
            Amount = PaymentRequests.AmountOf(payment.Amount, payment.Currency),
            ProviderData = payment.ProviderData,
            SaveCard = payment.SaveCard,
            ProviderCustomer = await requests.ProviderCustomerAsync(payment.CustomerId, connectionId, cancellationToken),
            ReturnValues = returnValues,
        }, CancellationToken.None);
        return await outcomes.ApplyAsync(payment.Id, outcome, CancellationToken.None);
    }

    /// <summary>Step 1 of PAY-8: the invoice can take a payment, a connection serves its currency, and the payment is recorded before any call.</summary>
    private async Task<Result<(Payment Payment, ProviderConnection Connection)>> WriteAheadAsync(StartInvoicePayment start, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockInvoiceAsync(start.InvoiceId, cancellationToken);
        if (await context.FindInvoiceAsync(start.InvoiceId, cancellationToken) is not { } invoice)
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

        if (await context.RoutedConnectionAsync(invoice.Currency, cancellationToken) is not { } connection)
        {
            return PaymentErrors.CurrencyNotRouted;
        }

        var now = clock.GetUtcNow();
        ConsentRecord? consent = null;
        if (start.SaveCard && start.Consent is { } given)
        {
            consent = ConsentRecord.Create(invoice.CustomerId, ConsentType.CardStorage, given.TextShown, new Dictionary<string, string>
            {
                ["invoiceId"] = invoice.Id.ToString(),
                ["amount"] = invoice.AmountRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["currency"] = invoice.Currency,
            }, given.IpAddress, given.UserAgent, now);
            context.ConsentRecords.Add(consent);
        }

        var payment = Payment.ForCustomer(
            invoice.CustomerId,
            invoice.Id,
            null,
            connection.Id,
            invoice.AmountRemaining,
            invoice.Currency,
            PaymentPurpose.Charge,
            start.SaveCard && consent is not null,
            consent?.Id,
            now + options.Value.CustomerActionTimeout,
            request.Caller.ToActor(),
            now);
        context.Payments.Add(payment);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return InvoiceErrors.PaymentInProgress;
        }

        await transaction.CommitAsync(cancellationToken);
        return (payment, connection);
    }
}
