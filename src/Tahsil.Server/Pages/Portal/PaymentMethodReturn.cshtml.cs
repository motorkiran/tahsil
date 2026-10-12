using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;
using Tahsil.Domain.Portal;
using Tahsil.Server.Hosting;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Pages.Portal;

/// <summary>
/// Where the provider sends the customer back after a card setup, by GET or POST, without anti-forgery: the setup is
/// completed, and the customer goes on, out of the provider's frame.
/// </summary>
[IgnoreAntiforgeryToken]
[RunsScripts]
public sealed class PaymentMethodReturnModel(PortalPages pages, PortalCards cards, RequestThrottle throttle, IOptions<MerchantOptions> merchant, IOptions<BillingOptions> billing)
    : PortalPageModel(pages, throttle, merchant, billing)
{
    /// <summary>Where the customer goes next.</summary>
    public string NextUrl { get; private set; } = string.Empty;

    public Task<IActionResult> OnGetAsync(string token, Guid paymentId, CancellationToken cancellationToken) =>
        CompleteAsync(token, paymentId, Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal), cancellationToken);

    public async Task<IActionResult> OnPostAsync(string token, Guid paymentId, CancellationToken cancellationToken) =>
        await CompleteAsync(
            token,
            paymentId,
            Request.HasFormContentType
                ? (await Request.ReadFormAsync(cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal)
                : new Dictionary<string, string>(),
            cancellationToken);

    private async Task<IActionResult> CompleteAsync(string token, Guid paymentId, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (await OpenAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        if (await cards.ReturnAsync(View.Session, paymentId, values, cancellationToken) is not var (state, failure))
        {
            return NotFound();
        }

        var page = $"/portal/{token}/payment-method";
        NextUrl = state switch
        {
            // A session opened to update the card goes back to the application (PRT-2).
            CardAdded.Saved => View.Session.Flow == PortalFlow.PaymentMethodUpdate && ReturnUrl is { } returnUrl ? returnUrl : $"{page}?done=added",
            CardAdded.Pending => $"{page}?done=pending",
            _ => $"{page}?failed={Uri.EscapeDataString(failure ?? "provider_error")}",
        };
        return Page();
    }
}
