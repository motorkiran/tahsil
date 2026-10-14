using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Model = Tahsil.Domain.Payments;
using ModelInvoices = Tahsil.Domain.Invoices;

namespace Tahsil.Application.Connections;

/// <summary>Provider settings from the <c>Tahsil:Providers</c> configuration section.</summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Tahsil:Providers";

    /// <summary>
    /// What an operator's test payment charges (ADP-18), in minor units of its currency; the provider's least amount
    /// when that is more.
    /// </summary>
    public long SmokeTestAmount { get; set; } = 100;

    internal bool IsValid() => SmokeTestAmount > 0;
}

/// <summary>A test payment to make: its invoice and the page where the operator pays it with a real card.</summary>
public sealed record ConnectionTestPayment(Guid InvoiceId, Uri Url, long Amount, string Currency);

/// <summary>
/// The operator's test payment of a connection (ADP-18): a small invoice of a customer kept for the purpose, paid on
/// its hosted page the way customers pay, through the connection its currency is routed to, and refunded as soon as
/// it is paid. It confirms the connection end to end, and the connection keeps when it last passed.
/// </summary>
public sealed class StartConnectionTestPaymentHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IBillingLocks locks,
    ISecretEncryption encryption,
    InvoiceFinalizer finalizer,
    InvoiceVoiding voiding,
    InvoiceLinks links,
    ProviderRegistry providers,
    IOptions<ProviderOptions> options,
    IOptions<ServerOptions> server,
    BillingClock clock)
{
    /// <summary>The external ID of the customer the test invoices belong to.</summary>
    public const string CustomerExternalId = "tahsil-connection-test";

    /// <summary>The metadata key that marks a test invoice, with its connection's ID.</summary>
    public const string MetadataKey = "tahsil_connection_test";

    public static readonly Error NotRouted = Error.Conflict(
        "connection.not_routed", "No currency is routed to the connection. Route one to it first: the test payment is taken the way customers' payments are.");

    public static readonly Error Disabled = Error.Conflict("connection.disabled", "The connection is disabled; enable it to take a test payment.");

    public async Task<Result<ConnectionTestPayment>> HandleAsync(string idOrKey, string? currency, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindConnectionAsync(idOrKey, cancellationToken) is not { } connection)
        {
            return Model.ConnectionErrors.NotFound;
        }

        if (!connection.IsEnabled)
        {
            return Disabled;
        }

        var routed = await context.RoutedCurrenciesAsync(connection.Id, cancellationToken);
        var chosen = routed.Count == 0 ? null : currency is null ? routed[0] : routed.FirstOrDefault(code => string.Equals(code, currency, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            return NotRouted;
        }

        var now = clock.GetUtcNow();
        var minimum = providers.Find(connection.Provider)?.Descriptor.MinimumAmounts.GetValueOrDefault(chosen) ?? 0;
        var amount = Math.Max(options.Value.SmokeTestAmount, minimum);

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        var customer = await CustomerAsync(now, cancellationToken);

        // An earlier test that was never paid is of no more use.
        var marker = connection.Id.ToString();
        var abandoned = await context.Invoices
            .Where(row => row.CustomerId == customer.Id && row.Status == ModelInvoices.InvoiceStatus.Open)
            .ToListAsync(cancellationToken);
        foreach (var stale in abandoned.Where(row => row.Metadata.GetValueOrDefault(MetadataKey) == marker))
        {
            await locks.LockInvoiceAsync(stale.Id, cancellationToken);
            await voiding.VoidAsync(stale, Actor.System, now, cancellationToken);
        }

        var invoice = ModelInvoices.Invoice.CreateDraft(
            customer.Id,
            chosen,
            ModelInvoices.BillingReason.Manual,
            ModelInvoices.CollectionMethod.SendInvoice,
            daysUntilDue: 365,
            memo: $"Test payment of the {connection.Name} connection. It is refunded as soon as it is paid.",
            autoAdvance: false,
            new Dictionary<string, string> { [MetadataKey] = marker },
            now).Value;
        invoice.AddLines([new ModelInvoices.InvoiceLineDraft(ModelInvoices.InvoiceLineType.Item, "Connection test payment", 1, amount, UnitAmountDecimal: amount)], customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        if (await finalizer.FinalizeAsync(invoice, cancellationToken, applyBalance: false) is { IsFailure: true } failed)
        {
            return failed.Error;
        }

        audit.Record(AuditActions.ConnectionTestPaymentStarted, AuditSubject.Of(AuditSubjects.Connection, connection.Id), new Dictionary<string, object?>
        {
            ["key"] = connection.Key,
            ["invoiceId"] = invoice.Id,
            ["amount"] = amount,
            ["currency"] = chosen,
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConnectionTestPayment(invoice.Id, links.UrlFor(invoice)!, amount, chosen);
    }

    /// <summary>The customer test invoices belong to, created with the first test: without an email, so nothing is sent.</summary>
    private async Task<Customer> CustomerAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await context.Customers.SingleOrDefaultAsync(row => row.ExternalId == CustomerExternalId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var customer = Customer.Create(
            CustomerExternalId,
            new CustomerDetails { Name = "Connection test payments", Type = Domain.Customers.CustomerType.Company, Locale = server.Value.DefaultLocale },
            (id, taxId) => encryption.Encrypt(taxId, SecretPurposes.CustomerTaxId, id),
            now).Value;
        context.Customers.Add(customer);

        // Saved now: finalization reads the invoice's customer.
        await context.SaveChangesAsync(cancellationToken);
        return customer;
    }
}

/// <summary>A test payment succeeded; it is refunded in full after the outcome is recorded, and the connection remembers that it passed.</summary>
[OutboxMessage("connections.refund_test_payment")]
public sealed record RefundConnectionTestPayment(Guid PaymentId);

public sealed partial class ConnectionTestRefunder(ITahsilDbContext context, Refunds refunds, IAuditLog audit, BillingClock clock, ILogger<ConnectionTestRefunder> logger)
    : IOutboxMessageHandler<RefundConnectionTestPayment>
{
    public async Task HandleAsync(RefundConnectionTestPayment message, CancellationToken cancellationToken)
    {
        var payment = await context.Payments.AsNoTracking().SingleAsync(row => row.Id == message.PaymentId, cancellationToken);
        var connection = await context.ProviderConnections.SingleAsync(row => row.Id == payment.ConnectionId, cancellationToken);
        if (connection.LastTestPaymentAt is null || connection.LastTestPaymentAt < payment.SucceededAt)
        {
            connection.RecordTestPayment(payment.SucceededAt ?? clock.GetUtcNow());
            audit.Record(AuditActions.ConnectionTestPaymentSucceeded, AuditSubject.Of(AuditSubjects.Connection, connection.Id), new Dictionary<string, object?>
            {
                ["key"] = connection.Key,
                ["paymentId"] = payment.Id,
                ["amount"] = payment.Amount,
                ["currency"] = payment.Currency,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        // Delivery is at least once: a refund of the test that exists ends here.
        if (await context.Refunds.AnyAsync(row => row.PaymentId == payment.Id && row.Reason == Model.RefundReason.ConnectionTest && row.Status != Model.RefundStatus.Failed, cancellationToken))
        {
            return;
        }

        var refunded = await refunds.CreateAsync(payment.Id, null, Model.RefundReason.ConnectionTest, "Refunded automatically: the test payment of a connection.", cancellationToken);
        if (refunded.IsFailure)
        {
            LogNotRefunded(logger, payment.Id, refunded.Error.Code);
        }
    }

    [LoggerMessage(LogLevel.Warning, "The test payment {PaymentId} of a connection was not refunded automatically: {Code}; refund it in the admin panel")]
    private static partial void LogNotRefunded(ILogger logger, Guid paymentId, string code);
}
