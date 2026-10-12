using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Application.Subscriptions;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Notifications;

/// <summary>Amounts and dates in a customer's language and the billing time zone (NTF-5).</summary>
public sealed class EmailFormat(IOptions<BillingOptions> billing)
{
    public static string Money(long amount, string currency, Customer customer)
    {
        var exponent = Domain.Money.Currency.TryGet(currency, out var known) ? known.Exponent : 2;
        return Formatting.Money(amount, currency, exponent, Formatting.CultureFor(customer.Locale));
    }

    public string Date(DateTimeOffset instant, Customer customer) => Formatting.Date(instant, billing.Value.Zone, Formatting.CultureFor(customer.Locale));
}

/// <summary>An invoice was paid (NTF §1 receipt): its lines, tax, total and how it was paid.</summary>
public sealed class ReceiptComposer(ITahsilDbContext context, InvoiceLinks links, EmailFormat format) : ICustomerEmailComposer
{
    public string Type => EmailTypes.Receipt;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var invoice = await context.Invoices.AsNoTracking().Include(row => row.Lines).SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        if (invoice.Status != InvoiceStatus.Paid)
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        var payment = await context.Payments.AsNoTracking().Where(row => row.InvoiceId == invoice.Id && row.Status == PaymentStatus.Succeeded)
            .OrderByDescending(row => row.SucceededAt).FirstOrDefaultAsync(cancellationToken);
        var card = payment?.PaymentMethodId is { } methodId ? await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(row => row.Id == methodId, cancellationToken) : null;
        return new ComposedEmail(customer, "receipt", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["invoice"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number"] = invoice.Number,
                ["lines"] = invoice.Lines.OrderBy(line => line.SortOrder).Select(line => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["description"] = line.Description,
                    ["amount"] = EmailFormat.Money(line.Total, invoice.Currency, customer),
                }).ToList(),
                ["tax"] = invoice.TaxTotal != 0 ? EmailFormat.Money(invoice.TaxTotal, invoice.Currency, customer) : null,
                ["total"] = EmailFormat.Money(invoice.Total, invoice.Currency, customer),
                ["paid"] = EmailFormat.Money(invoice.AmountPaid, invoice.Currency, customer),
                ["paid_at"] = invoice.PaidAt is { } paidAt ? format.Date(paidAt, customer) : null,
                ["card"] = card is null ? null : $"{card.Brand} •••• {card.Last4}",
                ["legal_invoice_number"] = invoice.LegalInvoiceNumber,
                ["hosted_url"] = links.UrlFor(invoice)?.AbsoluteUri,
            },
        });
    }
}

/// <summary>
/// An automatic payment failed for the customer (DUN §7): why, what happens next, and the invoice page to pay with
/// another card; or, for <see cref="EmailTypes.PaymentActionRequired"/>, that the bank wants the customer.
/// </summary>
public sealed class PaymentFailedComposer(ITahsilDbContext context, InvoiceLinks links, EmailFormat format, Portal.PortalLinks portal) : ICustomerEmailComposer
{
    public string Type => EmailTypes.PaymentFailed;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var payment = await context.Payments.AsNoTracking().SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == payment.InvoiceId, cancellationToken);
        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Uncollectible))
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        return new ComposedEmail(customer, "payment-failed", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["portal_url"] = portal.Create(customer.Id, Domain.Portal.PortalFlow.PaymentMethodUpdate).AbsoluteUri,
            ["invoice"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number"] = invoice.Number,
                ["amount"] = EmailFormat.Money(invoice.AmountRemaining, invoice.Currency, customer),
                ["hosted_url"] = links.UrlFor(invoice)?.AbsoluteUri,
            },
            ["failure"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message"] = FailureMessages.For(payment.FailureCode, customer.Locale),
                ["new_card_needed"] = payment.FailureCategory == "new_payment_method",
                ["next_attempt"] = invoice.NextPaymentAttemptAt is { } next ? format.Date(next, customer) : null,
            },
        });
    }
}

public sealed class PaymentActionRequiredComposer(ITahsilDbContext context, InvoiceLinks links) : ICustomerEmailComposer
{
    public string Type => EmailTypes.PaymentActionRequired;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var payment = await context.Payments.AsNoTracking().SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == payment.InvoiceId, cancellationToken);
        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Uncollectible))
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        return new ComposedEmail(customer, "payment-action-required", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["invoice"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number"] = invoice.Number,
                ["amount"] = EmailFormat.Money(invoice.AmountRemaining, invoice.Currency, customer),
                ["hosted_url"] = links.UrlFor(invoice)?.AbsoluteUri,
            },
        });
    }
}

/// <summary>The last reminder before dunning's final action (DUN §7): the cancellation date if the invoice stays unpaid.</summary>
public sealed class DunningFinalReminderComposer(ITahsilDbContext context, InvoiceLinks links, EmailFormat format) : ICustomerEmailComposer
{
    public string Type => EmailTypes.DunningFinalReminder;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        if (invoice is not { Status: InvoiceStatus.Open, DunningEndedAt: null, DunningDeadlineAt: { } deadline })
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        return new ComposedEmail(customer, "dunning-final-reminder", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["invoice"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number"] = invoice.Number,
                ["amount"] = EmailFormat.Money(invoice.AmountRemaining, invoice.Currency, customer),
                ["hosted_url"] = links.UrlFor(invoice)?.AbsoluteUri,
            },
            ["cancel_date"] = invoice.SubscriptionId is null ? null : format.Date(deadline, customer),
        });
    }
}

/// <summary>A subscription ended (NTF §1): why and when; a legally relevant notice (NTF-4).</summary>
public sealed class SubscriptionCanceledComposer(ITahsilDbContext context, SubscriptionLines lines, EmailFormat format) : ICustomerEmailComposer
{
    public string Type => EmailTypes.SubscriptionCanceled;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        if (subscription is not { Status: Domain.Subscriptions.SubscriptionStatus.Canceled, EndedAt: { } endedAt })
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        return new ComposedEmail(customer, "subscription-canceled", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["subscription"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plan"] = await PlanAsync(lines, subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken),
                ["ended_at"] = format.Date(endedAt, customer),
                ["reason"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(subscription.CancellationReason?.ToString() ?? "other"),
            },
        });
    }

    /// <summary>The items as the customer knows them, such as <c>Pro (Monthly) × 5</c>.</summary>
    internal static async Task<string> PlanAsync(SubscriptionLines lines, IEnumerable<(Guid PriceId, int? Quantity)> items, CancellationToken cancellationToken) =>
        string.Join(", ", (await lines.PricedAsync(items, cancellationToken)).Select(item =>
            $"{PriceDisplay.Name(item.Product.Name, item.Price.Name)}{(item.Quantity is { } quantity ? $" × {quantity.ToString(CultureInfo.InvariantCulture)}" : null)}"));
}

/// <summary>A refund succeeded (NTF §1): how much, of which payment, and when it typically arrives. Not for money returned outside Tahsil.</summary>
public sealed class RefundIssuedComposer(ITahsilDbContext context, EmailFormat format) : ICustomerEmailComposer
{
    public string Type => EmailTypes.RefundIssued;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var refund = await context.Refunds.AsNoTracking().SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        var payment = await context.Payments.AsNoTracking().SingleAsync(row => row.Id == refund.PaymentId, cancellationToken);
        if (refund.Kind == RefundKind.OutOfBand)
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == payment.CustomerId, cancellationToken);
        var card = payment.PaymentMethodId is { } methodId ? await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(row => row.Id == methodId, cancellationToken) : null;
        var invoice = payment.InvoiceId is { } invoiceId ? await context.Invoices.AsNoTracking().SingleOrDefaultAsync(row => row.Id == invoiceId, cancellationToken) : null;
        return new ComposedEmail(customer, "refund-issued", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["refund"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["amount"] = EmailFormat.Money(refund.Amount, refund.Currency, customer),
                ["payment_date"] = payment.SucceededAt is { } paidAt ? format.Date(paidAt, customer) : null,
                ["payment_amount"] = EmailFormat.Money(payment.Amount, payment.Currency, customer),
                ["card"] = card is null ? null : $"{card.Brand} •••• {card.Last4}",
                ["invoice_number"] = invoice?.Number,
                ["void"] = refund.Kind == RefundKind.Void,
            },
        });
    }
}

/// <summary>A higher price takes effect at a renewal (SUB-34): the old and new amounts, the date, and how to cancel.</summary>
public sealed class PriceChangeComposer(ITahsilDbContext context, SubscriptionLines lines, EmailFormat format, Portal.PortalLinks portal) : ICustomerEmailComposer
{
    public string Type => EmailTypes.PriceChange;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        if (subscription is not { IsTerminal: false, ScheduledChange: { } change })
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        var before = subscription.Items.Select(item => (item.PriceId, item.Quantity)).ToList();
        var after = change.Items.Select(item => (item.PriceId, item.Quantity)).ToList();
        return new ComposedEmail(customer, "price-change", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["portal_url"] = portal.Create(customer.Id, Domain.Portal.PortalFlow.SubscriptionCancel, subscription.Id).AbsoluteUri,
            ["subscription"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plan"] = await SubscriptionCanceledComposer.PlanAsync(lines, before, cancellationToken),
                ["new_plan"] = await SubscriptionCanceledComposer.PlanAsync(lines, after, cancellationToken),
                ["old_amount"] = EmailFormat.Money(await PerPeriodAsync(before, cancellationToken), subscription.Currency, customer),
                ["new_amount"] = EmailFormat.Money(await PerPeriodAsync(after, cancellationToken), subscription.Currency, customer),
                ["effective_date"] = format.Date(change.EffectiveAt, customer),
            },
        });
    }

    private async Task<long> PerPeriodAsync(IEnumerable<(Guid PriceId, int? Quantity)> items, CancellationToken cancellationToken) =>
        (await lines.PricedAsync(items, cancellationToken))
            .Where(item => item.Price.UsageType != Domain.Catalog.UsageType.Metered && item.Quantity is > 0)
            .Sum(item => Domain.Money.PriceAmounts.For(item.Price, item.Quantity!.Value));
}
