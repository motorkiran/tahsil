using Microsoft.AspNetCore.Mvc;
using Tahsil.Application.Connections;
using Tahsil.Application.Invoices;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Pages.Invoices;

/// <summary>Where the provider sends the customer back after paying an invoice, by GET or POST, without anti-forgery or cookies.</summary>
[IgnoreAntiforgeryToken]
[RunsScripts]
public sealed class ReturnModel(InvoicePages pages, ProviderRegistry providers) : HostedPageModel, IContentSecurityPolicySources
{
    /// <summary>Where the customer goes next, when the return arrived inside the provider's frame.</summary>
    public string? Next { get; private set; }

    public Task<IActionResult> OnGetAsync(string token, CancellationToken cancellationToken) =>
        CompleteAsync(token, Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal), cancellationToken);

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken cancellationToken) =>
        await CompleteAsync(
            token,
            Request.HasFormContentType
                ? (await Request.ReadFormAsync(cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal)
                : new Dictionary<string, string>(),
            cancellationToken);

    private async Task<IActionResult> CompleteAsync(string token, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (await pages.FindAsync(token, cancellationToken) is not { } invoice || await pages.ReturnAsync(invoice, values, cancellationToken) is not { } result)
        {
            return NotFound();
        }

        var next = $"/invoices/{token}?{(result.Paid ? "paid=true" : result.FailureCode is { } code ? $"failed={Uri.EscapeDataString(code)}" : string.Empty)}";

        // Some providers, such as PayTR, load the return inside their frame: the whole window moves on (PTR-8).
        if (Request.Headers["Sec-Fetch-Dest"] == "iframe")
        {
            Next = next;
            return Page();
        }

        return Redirect(next);
    }

    /// <summary>Some providers send the customer back inside their frame (PTR-8).</summary>
    public CspSources GetCspSources() => new() { FrameAncestors = providers.ReturnFrameAncestors };
}
