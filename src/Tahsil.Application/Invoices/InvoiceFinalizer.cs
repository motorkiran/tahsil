using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;

namespace Tahsil.Application.Invoices;

/// <summary>
/// Finalizes drafts (INV-8) for every source: the API, the admin panel and, later, the billing engine. It runs in the
/// caller's transaction, which holds the invoice's lock, and takes the number's and the balance's locks itself.
/// </summary>
public sealed class InvoiceFinalizer(
    ITahsilDbContext context,
    IBillingLocks locks,
    IInvoiceNumbers numbers,
    ISecretEncryption encryption,
    IRequestContext request,
    IAuditLog audit,
    ProviderRegistry providers,
    IOptions<BillingOptions> billing,
    IOptions<InvoiceOptions> invoices,
    BillingClock clock)
{
    /// <param name="invoice">A draft, loaded under its lock.</param>
    /// <param name="cancellationToken">Cancels the finalization.</param>
    /// <param name="applyBalance">
    /// Whether the customer's balance applies; not for a checkout's invoice, which its payment already paid in full.
    /// </param>
    public async Task<Result> FinalizeAsync(Invoice invoice, CancellationToken cancellationToken, bool applyBalance = true)
    {
        if (invoice.CanFinalize() is { IsFailure: true } invalid)
        {
            return invalid;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        var now = clock.GetUtcNow();
        invoice.Recalculate(customer.IsTaxExempt, now);
        if (invoice.CanFinalize() is { IsFailure: true } empty)
        {
            return empty;
        }

        var zone = billing.Value.Zone;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var number = $"{invoices.Value.NumberPrefix}-{local.Year}-{await numbers.NextAsync(local.Year, cancellationToken):D6}";

        var outcome = BalanceApplication.Apply(0, invoice.Total, null);
        if (applyBalance)
        {
            await locks.LockBalanceAsync(customer.Id, invoice.Currency, cancellationToken);
            var balance = await context.CustomerBalances.SingleAsync(row => row.CustomerId == customer.Id && row.Currency == invoice.Currency, cancellationToken);
            var minimum = invoice.CollectionMethod == CollectionMethod.ChargeAutomatically ? await MinimumChargeAsync(invoice.Currency, cancellationToken) : null;
            outcome = BalanceApplication.Apply(balance.Amount, invoice.Total, minimum);
            var actor = request.Caller.ToActor();
            foreach (var change in outcome.Changes)
            {
                context.BalanceTransactions.Add(balance.Change(change.Amount, change.Type, invoice.Id, null, actor, now));
            }
        }

        var snapshot = new CustomerSnapshot(customer.Name, customer.Email, customer.Type, customer.TaxOffice, customer.Address, customer.Phone, customer.Locale);
        var taxId = customer.EncryptedTaxId is { } encrypted ? encryption.DecryptString(encrypted, SecretPurposes.CustomerTaxId, customer.Id) : null;
        var finalized = invoice.Finalize(
            new InvoiceFinalization(
                number,
                snapshot,
                taxId is null ? null : invoiceId => encryption.Encrypt(taxId, SecretPurposes.CustomerTaxId, invoiceId),
                outcome,
                DueDate(local, invoice.DaysUntilDue ?? invoices.Value.DefaultDaysUntilDue, zone)),
            now);
        if (finalized.IsFailure)
        {
            return finalized;
        }

        var subject = AuditSubject.Of(AuditSubjects.Invoice, invoice.Id);
        audit.Record(AuditActions.InvoiceFinalized, subject, Details(invoice), customerId: invoice.CustomerId);
        if (invoice.Status == InvoiceStatus.Paid)
        {
            audit.Record(AuditActions.InvoicePaid, subject, Details(invoice), customerId: invoice.CustomerId);
        }

        return Result.Success();
    }

    /// <summary>What events about an invoice carry (EVT-1).</summary>
    internal static Dictionary<string, object?> Details(Invoice invoice) => new()
    {
        ["number"] = invoice.Number,
        ["total"] = invoice.Total,
        ["amountDue"] = invoice.AmountDue,
        ["amountPaid"] = invoice.AmountPaid,
        ["amountRemaining"] = invoice.AmountRemaining,
        ["currency"] = invoice.Currency,
        ["dueDate"] = invoice.DueDate,
        ["subscriptionId"] = invoice.SubscriptionId,
        ["billingReason"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(invoice.BillingReason.ToString()),
    };

    /// <summary>The end of the due day, <paramref name="days"/> days after finalization in the billing time zone.</summary>
    private static DateTimeOffset DueDate(DateTimeOffset finalizedLocal, int days, TimeZoneInfo zone)
    {
        var nextDay = finalizedLocal.Date.AddDays(days + 1);
        return new DateTimeOffset(nextDay, zone.GetUtcOffset(nextDay)).AddTicks(-TimeSpan.TicksPerMicrosecond);
    }

    /// <summary>
    /// The least amount the connection that would collect the invoice charges (MNY-23): the connection routed for the
    /// currency, until payment methods choose their own.
    /// </summary>
    private async Task<long?> MinimumChargeAsync(string currency, CancellationToken cancellationToken) =>
        await context.RoutedConnectionAsync(currency, cancellationToken) is { } connection
        && providers.Find(connection.Provider)?.Descriptor.MinimumAmounts.TryGetValue(currency, out var minimum) == true
            ? minimum
            : null;
}
