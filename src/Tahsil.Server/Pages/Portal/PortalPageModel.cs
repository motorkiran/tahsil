using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Pages.Portal;

/// <summary>
/// What every portal page does first (PRT-1, PRT-12): the limits per address and per session, then the session of the
/// token. An unknown token is not found; an expired one is not found either, on a page that leads back to the application.
/// </summary>
public abstract class PortalPageModel(PortalPages pages, RequestThrottle throttle, IOptions<MerchantOptions> merchant, IOptions<BillingOptions> billing) : HostedPageModel
{
    public PortalView View { get; private set; } = null!;

    public string Token { get; private set; } = string.Empty;

    /// <summary>Where "Return to the merchant" leads; none for a session made for an email.</summary>
    public string? ReturnUrl { get; private set; }

    public MerchantOptions Merchant => merchant.Value;

    public CultureInfo Culture => Formatting.CultureFor(View.Locale);

    protected PortalPages Pages => pages;

    /// <summary>
    /// Where a finished task leads (PRT-2): back to the application when the session was opened for that task, else to
    /// the overview with <paramref name="notice"/>.
    /// </summary>
    protected IActionResult Done(Domain.Portal.PortalFlow task, string notice) =>
        View.Session.Flow == task && ReturnUrl is { } returnUrl ? Redirect(returnUrl) : Redirect($"/portal/{Token}?done={notice}");

    public string Money(long amount, string currency) =>
        Formatting.Money(amount, currency, Domain.Money.Currency.TryGet(currency, out var known) ? known.Exponent : 2, Culture);

    /// <summary>A usage quantity in the page's language, with the decimals it has.</summary>
    public string Number(decimal quantity) => quantity.ToString("#,0.########", Culture);

    public string Date(DateTimeOffset instant) => Formatting.Date(instant, billing.Value.Zone, Culture);

    /// <summary>Loads the session's view, or returns what answers the request instead.</summary>
    protected async Task<IActionResult?> OpenAsync(string token, CancellationToken cancellationToken)
    {
        Token = token;
        if (!throttle.TryAcquire(ThrottledAction.HostedPage, HttpContext))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (await pages.FindAsync(token, cancellationToken) is not { } session)
        {
            return NotFound();
        }

        ReturnUrl = session.ReturnUrl;
        if (!pages.IsOpen(session))
        {
            // The error page says the link has expired, and leads back to the application (PRT-1).
            HttpContext.Items[ErrorModel.ReturnLinkKey] = new ReturnLink(session.ReturnUrl, merchant.Value.DisplayName);
            return NotFound();
        }

        if (!throttle.TryAcquire(ThrottledAction.HostedPage, $"portal:{session.Id}"))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        View = await pages.ViewAsync(session, Locale, cancellationToken);
        return null;
    }

}
