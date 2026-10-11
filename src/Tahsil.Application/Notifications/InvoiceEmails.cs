using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Invoices;

namespace Tahsil.Application.Notifications;

/// <summary>What invoice emails show of an open invoice: amounts, the due date, the hosted page and bank transfer details (NTF-10).</summary>
public sealed class InvoiceEmailModels(ITahsilDbContext context, InvoiceLinks links, IOptions<InvoiceOptions> invoices, IOptions<BillingOptions> billing)
{
    /// <summary>The invoice's email model, or <see langword="null"/> when it is no longer open.</summary>
    public async Task<ComposedEmail?> ForOpenAsync(Guid invoiceId, string template, CancellationToken cancellationToken)
    {
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId, cancellationToken);
        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Uncollectible))
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        var culture = Formatting.CultureFor(customer.Locale);
        var exponent = Domain.Money.Currency.TryGet(invoice.Currency, out var currency) ? currency.Exponent : 2;
        var bank = invoices.Value.BankTransfer.GetValueOrDefault(invoice.Currency);
        var model = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["invoice"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number"] = invoice.Number,
                ["amount"] = Formatting.Money(invoice.AmountRemaining, invoice.Currency, exponent, culture),
                ["total"] = Formatting.Money(invoice.Total, invoice.Currency, exponent, culture),
                ["due_date"] = invoice.DueDate is { } due ? Formatting.Date(due, billing.Value.Zone, culture) : null,
                ["hosted_url"] = links.UrlFor(invoice)?.AbsoluteUri,
            },
            ["bank_transfer"] = bank?.Iban is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["bank"] = bank.Bank,
                ["account_holder"] = bank.AccountHolder,
                ["iban"] = bank.Iban,
            },
        };
        return new ComposedEmail(customer, template, model);
    }
}

/// <summary>An invoice to pay by link or transfer was issued (INV-12).</summary>
public sealed class InvoiceIssuedComposer(InvoiceEmailModels models) : ICustomerEmailComposer
{
    public string Type => EmailTypes.InvoiceIssued;

    public Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken) =>
        models.ForOpenAsync(message.SubjectId, "invoice-issued", cancellationToken);
}

/// <summary>An invoice is due soon, today, or overdue (INV-12).</summary>
public sealed class InvoiceReminderComposer(InvoiceEmailModels models) : ICustomerEmailComposer
{
    public string Type => EmailTypes.InvoiceReminder;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        if (await models.ForOpenAsync(message.SubjectId, "invoice-reminder", cancellationToken) is not { } email)
        {
            return null;
        }

        var days = (int)Math.Round(double.Parse(message.Data!["offsetDays"], CultureInfo.InvariantCulture));
        email.Model["reminder"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["days_left"] = days < 0 ? -days : 0,
            ["due_today"] = days == 0,
            ["days_overdue"] = days > 0 ? days : 0,
        };
        return email;
    }
}
