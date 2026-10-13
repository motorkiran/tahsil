using Microsoft.AspNetCore.Mvc;
using Tahsil.Application.Checkout;
using Tahsil.Application.Connections;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Pages.Checkout;

/// <summary>
/// Where the provider sends the customer back (CHK-19), by GET or POST, without anti-forgery or cookies: the payment is
/// completed, then the customer goes to the merchant, waits for the result, or tries again.
/// </summary>
[IgnoreAntiforgeryToken]
[RunsScripts]
public sealed class ReturnModel(CheckoutPages pages, ProviderRegistry providers) : HostedPageModel, IContentSecurityPolicySources
{
    public string Token { get; private set; } = string.Empty;

    public CheckoutReturn Result { get; private set; } = null!;

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
        Token = token;
        if (await pages.ReturnAsync(token, paymentId, values, cancellationToken) is not { } result)
        {
            return NotFound();
        }

        Result = result;
        return result.State == CheckoutReturnState.Failed ? Redirect($"/checkout/{token}?failed={Uri.EscapeDataString(result.FailureCode ?? "provider_error")}") : Page();
    }

    /// <summary>Some providers send the customer back inside their frame (PTR-8).</summary>
    public CspSources GetCspSources() => new() { FrameAncestors = providers.ReturnFrameAncestors };
}
