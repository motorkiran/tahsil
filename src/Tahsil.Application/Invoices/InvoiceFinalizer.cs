using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Coupons;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Coupons;
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
    Subscriptions.SubscriptionSettlement subscriptions,
    InvoiceReminders reminders,
    Redemptions redemptions,
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
        var discount = await DiscountAsync(invoice, customer.IsTaxExempt, now, cancellationToken);
        if (discount.IsFailure)
        {
            return discount.Error;
        }

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

        if (discount.Value is { } used)
        {
            var coupon = await context.Coupons.SingleAsync(row => row.Id == used.CouponId, cancellationToken);
            used.RecordFinalized(coupon, invoice.BillingReason, now);
        }

        var subject = AuditSubject.Of(AuditSubjects.Invoice, invoice.Id);
        audit.Record(AuditActions.InvoiceFinalized, subject, Details(invoice), customerId: invoice.CustomerId);
        if (invoice.Status == InvoiceStatus.Paid)
        {
            audit.Record(AuditActions.InvoicePaid, subject, Details(invoice), customerId: invoice.CustomerId);
            await subscriptions.InvoicePaidAsync(invoice, now, cancellationToken);
        }
        else if (invoice.CollectionMethod == CollectionMethod.SendInvoice)
        {
            reminders.Issued(invoice, now);
        }

        return Result.Success();
    }

    /// <summary>
    /// The discount the invoice is finalized with (CPN-12 to CPN-14): a subscription's while it lasts, dropped from the
    /// draft once it ended; a manual invoice's coupon is redeemed now, which counts it (CPN-9).
    /// </summary>
    private async Task<Result<Discount?>> DiscountAsync(Invoice invoice, bool taxExempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invoice.Discount is not { } applied)
        {
            return Result.Success<Discount?>(null);
        }

        if (applied.DiscountId is { } discountId)
        {
            // Found among the changes not yet saved too: a checkout creates the discount with its invoice.
            var current = await context.Discounts.FindAsync([discountId], cancellationToken)
                ?? throw new InvalidOperationException($"Invoice {invoice.Id} names discount {discountId}, which does not exist.");
            if (current.IsActive)
            {
                return current;
            }

            invoice.SetDiscount(null, taxExempt, now);
            return Result.Success<Discount?>(null);
        }

        var productIds = invoice.Lines.Where(line => line.ProductId is not null).Select(line => line.ProductId!.Value).Distinct().ToList();
        var redeemed = await redemptions.RedeemAsync(
            applied.CouponId, null, invoice.CustomerId, new RedemptionTarget(invoice.Currency, productIds, invoice.Lines.Sum(line => line.Amount)), cancellationToken);
        if (redeemed.IsFailure)
        {
            return redeemed.Error;
        }

        var discount = Discount.ForInvoice(redeemed.Value.Coupon, null, invoice.CustomerId, invoice.Id, now);
        redemptions.Add(discount, redeemed.Value);
        invoice.SetDiscount(Redemptions.ToInvoiceDiscount(redeemed.Value.Coupon, discount.Id, null), taxExempt, now);
        return discount;
    }

    /// <summary>
    /// What finalizing an invoice of <paramref name="total"/> would do with the customer's balance now, without locking
    /// or changing it (SUB-24).
    /// </summary>
    public async Task<BalanceOutcome> PreviewBalanceAsync(Guid customerId, string currency, long total, CollectionMethod collectionMethod, CancellationToken cancellationToken)
    {
        var balance = await context.CustomerBalances.AsNoTracking()
            .Where(row => row.CustomerId == customerId && row.Currency == currency)
            .Select(row => (long?)row.Amount)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var minimum = collectionMethod == CollectionMethod.ChargeAutomatically ? await MinimumChargeAsync(currency, cancellationToken) : null;
        return BalanceApplication.Apply(balance, total, minimum);
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
