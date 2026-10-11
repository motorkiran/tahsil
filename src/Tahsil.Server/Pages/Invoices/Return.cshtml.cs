using Microsoft.AspNetCore.Mvc;
using Tahsil.Application.Invoices;

namespace Tahsil.Server.Pages.Invoices;

/// <summary>Where the provider sends the customer back after paying an invoice, by GET or POST, without anti-forgery or cookies.</summary>
[IgnoreAntiforgeryToken]
public sealed class ReturnModel(InvoicePages pages) : HostedPageModel
{
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

        return Redirect($"/invoices/{token}?{(result.Paid ? "paid=true" : result.FailureCode is { } code ? $"failed={Uri.EscapeDataString(code)}" : string.Empty)}");
    }
}
