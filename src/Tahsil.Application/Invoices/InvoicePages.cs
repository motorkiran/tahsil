using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Invoices;

/// <summary>A tax rate's share of an invoice (INV-25).</summary>
public sealed record TaxBreakdown(decimal Percentage, bool Inclusive, long Taxable, long Tax);

/// <summary>A finalized invoice as its hosted page shows it (INV-25, INV-26).</summary>
public sealed record InvoicePageView(
    Invoice Invoice,
    string Locale,
    string Title,
    IReadOnlyList<TaxBreakdown> Taxes,
    IReadOnlyList<Payment> Payments,
    IReadOnlyList<Refund> Refunds,
    BankTransferOptions? BankTransfer,
    bool CanPay);

public sealed record InvoicePaymentReturn(bool Paid, string? FailureCode);

/// <summary>
/// The hosted invoice page's use cases (INV-24 to INV-27): reading a finalized invoice by its link, paying it by card
/// through the connection that serves its currency, and the provider's return.
/// </summary>
public sealed class InvoicePages(
    ITahsilDbContext context,
    InvoiceLinks links,
    IPageCaller caller,
    CustomerPayments payments,
    IPublicUrls urls,
    IOptions<InvoiceOptions> options,
    IOptions<ServerOptions> server)
{
    /// <summary>The finalized invoice of a link, or none for an invalid link or a draft.</summary>
    public async Task<Invoice?> FindAsync(string token, CancellationToken cancellationToken) =>
        links.Verify(token) is { } id && await context.Invoices.AsNoTracking().Include(invoice => invoice.Lines)
            .SingleOrDefaultAsync(invoice => invoice.Id == id, cancellationToken) is { Status: not InvoiceStatus.Draft } invoice
            ? invoice
            : null;

    /// <summary>The page's language: the customer's when the invoice was finalized.</summary>
    public string LocaleOf(Invoice invoice) => invoice.CustomerSnapshot?.Locale ?? server.Value.DefaultLocale;

    public async Task<InvoicePageView> ViewAsync(Invoice invoice, string locale, CancellationToken cancellationToken)
    {
        var paid = await context.Payments.AsNoTracking()
            .Where(payment => payment.InvoiceId == invoice.Id && payment.Status == PaymentStatus.Succeeded)
            .OrderBy(payment => payment.SucceededAt)
            .ToListAsync(cancellationToken);
        var paymentIds = paid.Select(payment => payment.Id).ToList();
        var refunds = await context.Refunds.AsNoTracking()
            .Where(refund => paymentIds.Contains(refund.PaymentId) && refund.Status == RefundStatus.Succeeded)
            .OrderBy(refund => refund.SucceededAt)
            .ToListAsync(cancellationToken);
        var taxes = invoice.Lines
            .Where(line => line.TaxPercentage is not null)
            .GroupBy(line => (Percentage: line.TaxPercentage!.Value, Inclusive: line.TaxInclusive == true))
            .Select(group => new TaxBreakdown(
                group.Key.Percentage, group.Key.Inclusive, group.Sum(line => line.Total - line.TaxAmount), group.Sum(line => line.TaxAmount)))
            .OrderByDescending(tax => tax.Percentage)
            .ToList();
        var canPay = invoice.Status is InvoiceStatus.Open or InvoiceStatus.Uncollectible && invoice.AmountRemaining > 0;
        return new InvoicePageView(
            invoice,
            locale,
            Title(invoice, locale),
            taxes,
            paid,
            refunds,
            canPay ? options.Value.BankTransfer.GetValueOrDefault(invoice.Currency) : null,
            canPay);
    }

    /// <summary>Starts a card payment of what is left of the invoice (INV-26, PAY-8).</summary>
    public async Task<Result<StartedPayment>> PayAsync(string token, Invoice invoice, IPAddress? ipAddress, CancellationToken cancellationToken)
    {
        caller.Use(new CustomerCaller(invoice.CustomerId, CustomerSessionKind.Invoice, invoice.Id));
        return await payments.StartAsync(
            new StartInvoicePayment(invoice.Id, urls.Build($"invoices/{token}/return"), urls.Build($"invoices/{token}"), SaveCard: false, Consent: null, ipAddress?.ToString()),
            cancellationToken);
    }

    /// <summary>The provider sent the customer back: the invoice's payment in flight is completed.</summary>
    public async Task<InvoicePaymentReturn?> ReturnAsync(Invoice invoice, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var latest = await context.Payments.AsNoTracking()
            .Where(payment => payment.InvoiceId == invoice.Id && payment.Initiator == PaymentInitiator.Customer)
            .OrderByDescending(payment => payment.CreatedAt).ThenByDescending(payment => payment.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return null;
        }

        caller.Use(new CustomerCaller(invoice.CustomerId, CustomerSessionKind.Invoice, invoice.Id));
        var completed = await payments.CompleteAsync(latest.Id, values, cancellationToken);
        return completed.IsFailure
            ? null
            : new InvoicePaymentReturn(completed.Value.Status == PaymentStatus.Succeeded, completed.Value.Status is PaymentStatus.Failed or PaymentStatus.Canceled ? completed.Value.FailureCode : null);
    }

    /// <summary>The document's title (INV-27): what it asks for while open, and a receipt once paid.</summary>
    private string Title(Invoice invoice, string locale)
    {
        var status = invoice.Status.ToString().ToLowerInvariant();
        if (options.Value.DocumentTitle.GetValueOrDefault(locale)?.GetValueOrDefault(status) is { } configured)
        {
            return configured;
        }

        return (invoice.Status, locale) switch
        {
            (InvoiceStatus.Paid, "tr") => "Tahsilat Makbuzu",
            (InvoiceStatus.Paid, _) => "Receipt",
            (InvoiceStatus.Void, "tr") => "İptal Edilmiş Ödeme Talebi",
            (InvoiceStatus.Void, _) => "Void invoice",
            (_, "tr") => "Ödeme Talebi",
            _ => "Invoice",
        };
    }
}
