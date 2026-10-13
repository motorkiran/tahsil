using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Domain.Invoices;
using Tahsil.Providers.Abstractions;
using Tahsil.Server.Hosting;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Pages.Invoices;

/// <summary>
/// The hosted invoice page (INV-24 to INV-28): the document with the seller, the customer, lines, taxes and payments, a
/// print layout, and for open invoices a card payment and bank transfer instructions.
/// </summary>
[RunsScripts]
public sealed class IndexModel(
    InvoicePages pages, RequestThrottle throttle, IOptions<MerchantOptions> merchant, IStringLocalizer<SharedResource> localizer) : HostedPageModel, IContentSecurityPolicySources
{
    public InvoicePageView View { get; private set; } = null!;

    public string Token { get; private set; } = string.Empty;

    public Presentation.Embed? Embed { get; private set; }

    /// <summary>The provider's direct card form, shown alone on a response without scripts (ADP-13).</summary>
    public Presentation.DirectForm? CardForm { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool JustPaid { get; private set; }

    public MerchantOptions Merchant => merchant.Value;

    public Invoice Invoice => View.Invoice;

    public CultureInfo Culture => Formatting.CultureFor(View.Locale);

    /// <summary>Tahsil's invoices are not legal invoices until one is recorded for them (INV-27).</summary>
    public bool ShowsTaxNote => Invoice.LegalInvoiceNumber is null;

    private ContentSecuritySources Sources { get; set; } = ContentSecuritySources.None;

    public string Money(long amount) => Formatting.Money(amount, Invoice.Currency, Domain.Money.Currency.TryGet(Invoice.Currency, out var currency) ? currency.Exponent : 2, Culture);

    public string Date(DateTimeOffset? instant) => instant is { } value ? Formatting.Date(value, HttpContext.RequestServices.GetRequiredService<IOptions<BillingOptions>>().Value.Zone, Culture) : string.Empty;

    public async Task<IActionResult> OnGetAsync(string token, string? failed, bool paid, CancellationToken cancellationToken)
    {
        if (!throttle.TryAcquire(ThrottledAction.HostedPage, HttpContext))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!await LoadAsync(token, cancellationToken))
        {
            return NotFound();
        }

        JustPaid = paid;
        if (failed is not null)
        {
            var failure = Enum.GetValues<FailureCode>().FirstOrDefault(candidate => FailureMessages.Name(candidate) == failed, FailureCode.ProviderError);
            ErrorMessage = View.Locale == "tr" ? FailureMessages.Turkish(failure) : FailureMessages.English(failure);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostPayAsync(string token, CancellationToken cancellationToken)
    {
        if (!throttle.TryAcquire(ThrottledAction.HostedPage, HttpContext))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!await LoadAsync(token, cancellationToken) || !View.CanPay)
        {
            return NotFound();
        }

        var started = await pages.PayAsync(token, Invoice, HttpContext.Connection.RemoteIpAddress, cancellationToken);
        if (started.IsFailure)
        {
            ErrorMessage = started.Error.Code == "invoice.payment_in_progress"
                ? localizer["A payment of this invoice is already in progress. Please wait a few minutes."]
                : localizer["The payment could not be started. Please try again."];
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        switch (started.Value.Presentation)
        {
            case Presentation.Redirect redirect:
                return Redirect(redirect.Url.AbsoluteUri);
            case Presentation.Embed embed:
                Embed = embed;
                return Page();
            case Presentation.DirectForm form:
                CardForm = form;
                return Page();
            default:
                throw new NotSupportedException("The provider presents the payment in a way the page does not know.");
        }
    }

    public CspSources GetCspSources() => new()
    {
        Scripts = Sources.Scripts,
        Frames = Sources.Frames,
        Connections = Sources.Connections,
        Images = Sources.Images,
        FormActions = Sources.FormActions,
        ForbidScripts = CardForm is not null,
    };

    private async Task<bool> LoadAsync(string token, CancellationToken cancellationToken)
    {
        Token = token;
        if (await pages.FindAsync(token, cancellationToken) is not { } invoice)
        {
            return false;
        }

        View = await pages.ViewAsync(invoice, Locale, cancellationToken);
        if (View.CanPay)
        {
            Sources = await HttpContext.RequestServices.GetRequiredService<ProviderSources>().ForCurrencyAsync(invoice.Currency, cancellationToken);
        }

        return true;
    }
}
