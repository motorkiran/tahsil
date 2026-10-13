using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Subscriptions;

/// <summary>The invoices of a subscription's periods (INV-1, INV-5).</summary>
public sealed class SubscriptionInvoices(
    ITahsilDbContext context,
    IAuditLog audit,
    SubscriptionLines lines,
    InvoiceLines invoiceLines,
    Usage.SubscriptionUsage usage,
    InvoiceFinalizer finalizer,
    Redemptions redemptions,
    IOptions<BillingOptions> billing)
{
    /// <summary>
    /// A new period's cycle invoice (INV-1): the licensed items for the period, the metered items' usage before it, then
    /// the subscription's pending items; a draft that finalizes itself after the draft period, during which items can
    /// still join it and usage of the period that ended can still be reported (USG-11). None when there is nothing to
    /// bill: usage reported later is then billed by the next invoice.
    /// </summary>
    public async Task<Invoice?> CreateCycleInvoiceAsync(Subscription subscription, Customer customer, BillingPeriod period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var drafts = await lines.LicensedAsync(subscription, period, customer.Locale, cancellationToken);
        drafts.AddRange(await usage.PreviewAsync(subscription, subscription.Items, period.Start, customer.Locale, cancellationToken));
        // When the worker is behind, an earlier period's invoice in this transaction took its items already, unsaved yet.
        var pending = (await context.PendingInvoiceItems
            .Where(item => item.SubscriptionId == subscription.Id && item.InvoiceId == null && item.Currency == subscription.Currency)
            .InJoiningOrder()
            .ToListAsync(cancellationToken)).Where(item => item.InvoiceId is null).ToList();
        drafts.AddRange(await invoiceLines.FromPendingItemsAsync(pending, customer.Locale, cancellationToken));
        if (drafts.Count == 0)
        {
            return null;
        }

        var invoice = Invoice.CreateDraft(
            customer.Id,
            subscription.Currency,
            BillingReason.SubscriptionCycle,
            subscription.CollectionMethod,
            subscription.DaysUntilDue,
            memo: null,
            autoAdvance: true,
            Metadata.Empty,
            now,
            subscription.Id,
            period,
            finalizeAt: now + billing.Value.DraftPeriod,
            discount: await redemptions.OfSubscriptionAsync(subscription.DiscountId, cancellationToken)).Value;
        invoice.AddLines(drafts, customer.IsTaxExempt, now);
        foreach (var item in pending)
        {
            item.IncludeIn(invoice.Id, now);
        }

        context.Invoices.Add(invoice);
        subscription.SetLatestInvoice(invoice.Id, now);
        var details = InvoiceFinalizer.Details(invoice);
        details["finalizeAt"] = invoice.FinalizeAt;
        audit.Record(AuditActions.InvoiceCreated, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), details, customerId: customer.Id);
        return invoice;
    }

    /// <summary>
    /// The final invoice of a subscription that ended at <paramref name="endedAt"/> (SUB-12, SUB-29): its metered usage
    /// until then, finalized at once. None without usage to bill. Returns whether it worked.
    /// </summary>
    public async Task<bool> CreateFinalUsageInvoiceAsync(Subscription subscription, Customer customer, DateTimeOffset endedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var used = await usage.InvoiceAsync(subscription, subscription.Items, endedAt, customer.Locale, cancellationToken);
        if (used.Count == 0)
        {
            return true;
        }

        var invoice = Invoice.CreateDraft(
            customer.Id,
            subscription.Currency,
            BillingReason.SubscriptionCancel,
            subscription.CollectionMethod,
            subscription.DaysUntilDue,
            memo: null,
            autoAdvance: false,
            Metadata.Empty,
            now,
            subscription.Id,
            discount: await redemptions.OfSubscriptionAsync(subscription.DiscountId, cancellationToken)).Value;
        invoice.AddLines(used, customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        subscription.SetLatestInvoice(invoice.Id, now);
        return (await finalizer.FinalizeAsync(invoice, cancellationToken)).IsSuccess;
    }

    /// <summary>
    /// Items leave the subscription at <paramref name="until"/>: their usage until then waits for the subscription's
    /// next invoice as pending items (SUB-25). Returns whether it worked.
    /// </summary>
    public async Task<bool> AddPendingUsageAsync(
        Subscription subscription, Customer customer, IReadOnlyCollection<SubscriptionItem> leaving, DateTimeOffset until, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var used = await usage.InvoiceAsync(subscription, leaving, until, customer.Locale, cancellationToken);
        foreach (var line in used)
        {
            context.PendingInvoiceItems.Add(PendingInvoiceItem.ForUsage(
                customer.Id, subscription.Id, subscription.Currency, line.Description, line.Amount, line.TaxRateId, line.PeriodStart!.Value, line.PeriodEnd!.Value, now));
        }

        if (used.Count > 0)
        {
            // Saved now, so that the renewal invoice that follows finds the items.
            await context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
