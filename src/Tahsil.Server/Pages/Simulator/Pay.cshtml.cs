using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Tahsil.Application.Common;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Pages.Simulator;

/// <summary>
/// The simulator's card page (SIM-3), embedded in Tahsil's own pages: test cards only (SIM-4), a simulated bank for
/// 3D Secure (SIM-5), and a return to the merchant in the top window (SIM-6).
/// </summary>
[RunsScripts]
public sealed class PayModel(SimulatorBank bank) : PageModel, IContentSecurityPolicySources
{
    public CardPage? Payment { get; private set; }

    public CardEntryError? Error { get; private set; }

    public string Amount => Payment is null ? string.Empty : Formatting.Money(Payment.Amount, Payment.Currency, Payment.Exponent, Formatting.CultureFor(CultureInfo.CurrentUICulture.Name));

    public bool IsSetup => Payment?.Purpose == PaymentPurpose.Setup;

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken cancellationToken)
    {
        Payment = await bank.ReadPageAsync(token, cancellationToken);
        return Payment is null ? NotFound() : Page();
    }

    // Model binding keeps the card number in this method only: it is never echoed, stored or logged unless it is a test card.
    public async Task<IActionResult> OnPostPayAsync(string token, string? number, string? expiry, string? cvc, bool saveCard, CancellationToken cancellationToken) =>
        await ShowAsync(token, await bank.SubmitCardAsync(token, new CardEntryForm(number, expiry, cvc, saveCard), cancellationToken), cancellationToken);

    public async Task<IActionResult> OnPostCancelAsync(string token, CancellationToken cancellationToken) =>
        await ShowAsync(token, await bank.CancelAsync(token, cancellationToken), cancellationToken);

    public async Task<IActionResult> OnPostAuthenticateAsync(string token, AuthenticationDecision decision, CancellationToken cancellationToken) =>
        await ShowAsync(token, await bank.AuthenticateAsync(token, decision, cancellationToken), cancellationToken);

    /// <summary>Tahsil's own pages embed this one; other sites cannot.</summary>
    public CspSources GetCspSources() => new() { FrameAncestors = ["'self'"] };

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context) => Response.Headers.XFrameOptions = "SAMEORIGIN";

    private async Task<IActionResult> ShowAsync(string token, CardPageResult result, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case CardPageResult.Invalid invalid:
                Error = invalid.Error;
                break;
            case CardPageResult.Authenticate:
                return RedirectToPage(new { token });
            case CardPageResult.NotFound:
                return NotFound();
        }

        Payment = await bank.ReadPageAsync(token, cancellationToken);
        return Payment is null ? NotFound() : Page();
    }
}
