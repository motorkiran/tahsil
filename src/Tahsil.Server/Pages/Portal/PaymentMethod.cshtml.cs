using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Application.Portal;
using Tahsil.Domain.Portal;
using Tahsil.Providers.Abstractions;
using Tahsil.Server.Hosting;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Pages.Portal;

/// <summary>
/// The customer's cards in the portal (PRT-11): adding a card in the provider's own fields with the consent to keep
/// it, making another card the default, and removing the others.
/// </summary>
[RunsScripts]
public sealed class PaymentMethodModel(
    PortalPages pages, PortalCards cards, RequestThrottle throttle, IOptions<MerchantOptions> merchant, IOptions<BillingOptions> billing, IStringLocalizer<SharedResource> localizer)
    : PortalPageModel(pages, throttle, merchant, billing), IContentSecurityPolicySources
{
    public PortalCardSetup Setup { get; private set; } = null!;

    /// <summary>The provider's fields, once the customer agreed to keep the card.</summary>
    public Presentation.Embed? Embed { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? Notice { get; private set; }

    public string ConsentText => cards.ConsentText(View.Locale);

    [BindProperty]
    public bool CardStorage { get; set; }

    [BindProperty]
    public Guid Card { get; set; }

    private ContentSecuritySources Sources { get; set; } = ContentSecuritySources.None;

    public async Task<IActionResult> OnGetAsync(string token, string? failed, string? done, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        if (failed is not null)
        {
            ErrorMessage = FailureMessage(failed);
        }

        Notice = done switch
        {
            "added" => localizer["Your card was saved and is now your default."].Value,
            "pending" => localizer["Your card is being confirmed. It appears here once it is saved."].Value,
            _ => null,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(string token, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        var started = await cards.AddAsync(
            View.Session, token, CardStorage, View.Locale, HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString(), cancellationToken);
        if (started.IsFailure)
        {
            ErrorMessage = started.Error.Code switch
            {
                "checkout.consent_required" => localizer["Tick the required boxes to continue."],
                "checkout.attempts_exceeded" => localizer["Too many payment attempts. Please try again later."],
                _ => localizer["The card could not be added. Please try again."],
            };
            Response.StatusCode = started.Error.Code == "checkout.attempts_exceeded" ? StatusCodes.Status429TooManyRequests : StatusCodes.Status400BadRequest;
            return Page();
        }

        switch (started.Value.Presentation)
        {
            case Presentation.Redirect redirect:
                return Redirect(redirect.Url.AbsoluteUri);
            case Presentation.Embed embed:
                Embed = embed;
                return Page();
            default:
                throw new NotSupportedException("Direct card forms come with the providers that use them.");
        }
    }

    public async Task<IActionResult> OnPostDefaultAsync(string token, CancellationToken cancellationToken) =>
        await LoadAsync(token, cancellationToken) ?? Changed(await cards.MakeDefaultAsync(View.Session, Card, cancellationToken), PortalFlow.PaymentMethodUpdate, "default");

    public async Task<IActionResult> OnPostRemoveAsync(string token, CancellationToken cancellationToken) =>
        await LoadAsync(token, cancellationToken) ?? Changed(await cards.RemoveAsync(View.Session, Card, cancellationToken), null, "removed");

    public CspSources GetCspSources() => new()
    {
        Scripts = Sources.Scripts,
        Frames = Sources.Frames,
        Connections = Sources.Connections,
        Images = Sources.Images,
        FormActions = Sources.FormActions,
    };

    private IActionResult Changed(Domain.Common.Result result, PortalFlow? task, string notice)
    {
        if (result.IsFailure)
        {
            // A card that is not the customer's does not exist for them (PRT-3).
            return result.Error.Code == PortalCardErrors.NotFound.Code ? NotFound() : Redirect($"/portal/{Token}/payment-method?failed={result.Error.Code}");
        }

        return task is { } flow ? Done(flow, notice) : Redirect($"/portal/{Token}/payment-method");
    }

    private async Task<IActionResult?> LoadAsync(string token, CancellationToken cancellationToken)
    {
        if (await OpenAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        if (!View.Has(PortalFeatures.PaymentMethod))
        {
            return NotFound();
        }

        Setup = await cards.SetupAsync(View.Session, cancellationToken);
        Sources = cards.SourcesOf(Setup);
        return null;
    }

    private string FailureMessage(string code)
    {
        if (code == PortalCardErrors.IsDefault.Code)
        {
            return localizer["Make another card the default before removing this one."];
        }

        if (code == CheckoutPageErrors.CardNotSaved.Code)
        {
            return localizer["The card was not saved. Please try again."];
        }

        var failure = Enum.GetValues<FailureCode>().FirstOrDefault(candidate => FailureMessages.Name(candidate) == code, FailureCode.ProviderError);
        return View.Locale == "tr" ? FailureMessages.Turkish(failure) : FailureMessages.English(failure);
    }
}
