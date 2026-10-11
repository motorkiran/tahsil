using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
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
    ITahsilDbContext context, IAuditLog audit, SubscriptionLines lines, InvoiceLines invoiceLines, IOptions<BillingOptions> billing)
{
    /// <summary>
    /// A new period's cycle invoice (INV-1): the licensed items for the period, then the subscription's pending items;
    /// a draft that finalizes itself after the draft period, during which items can still join it. None when there is
    /// nothing to bill.
    /// </summary>
    public async Task<Invoice?> CreateCycleInvoiceAsync(Subscription subscription, Customer customer, BillingPeriod period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var drafts = await lines.LicensedAsync(subscription, period, customer.Locale, cancellationToken);
        var pending = await context.PendingInvoiceItems
            .Where(item => item.SubscriptionId == subscription.Id && item.InvoiceId == null && item.Currency == subscription.Currency)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
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
            finalizeAt: now + billing.Value.DraftPeriod).Value;
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
}
