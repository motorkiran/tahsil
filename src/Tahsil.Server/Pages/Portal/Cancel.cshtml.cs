using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;
using Tahsil.Domain.Portal;
using Tahsil.Server.Hosting;
using CancellationFeedback = Tahsil.Contracts.CancellationFeedback;

namespace Tahsil.Server.Pages.Portal;

/// <summary>
/// Ending a subscription in the portal (PRT-8 to PRT-10): a retention offer when configured, declined with one click of
/// equal weight; then one optional question and one confirmation of the chosen way to end it.
/// </summary>
public sealed class CancelModel(
    PortalPages pages,
    PortalCancellation cancellation,
    RequestThrottle throttle,
    IOptions<MerchantOptions> merchant,
    IOptions<BillingOptions> billing,
    IStringLocalizer<SharedResource> localizer)
    : PortalPageModel(pages, throttle, merchant, billing)
{
    public CancelChoices Choices { get; private set; } = null!;

    /// <summary>Whether the retention offer is shown, before the choices.</summary>
    public bool OffersToStay { get; private set; }

    public string? ErrorMessage { get; private set; }

    [BindProperty]
    public CancelKind Choice { get; set; }

    [BindProperty]
    public CancellationFeedback? Feedback { get; set; }

    public async Task<IActionResult> OnGetAsync(string token, Guid id, bool declined, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, id, cancellationToken) is { } answered)
        {
            return answered;
        }

        OffersToStay = Choices.RetentionOffer is not null && !declined;
        Choice = Choices.Choices[0].Kind;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, Guid id, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, id, cancellationToken) is { } answered)
        {
            return answered;
        }

        var canceled = await cancellation.CancelAsync(
            View.Session, id, Choice, Feedback, View.Locale, HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString(), cancellationToken);
        if (canceled.IsFailure)
        {
            ErrorMessage = localizer["The subscription could not be canceled. Please try again."];
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        return Done(PortalFlow.SubscriptionCancel, Choice == CancelKind.PeriodEnd ? "cancel_scheduled" : "canceled");
    }

    public async Task<IActionResult> OnPostStayAsync(string token, Guid id, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, id, cancellationToken) is { } answered)
        {
            return answered;
        }

        return (await cancellation.AcceptOfferAsync(View.Session, id, View.Locale, cancellationToken)).IsSuccess
            ? Done(PortalFlow.SubscriptionCancel, "kept")
            : NotFound();
    }

    private async Task<IActionResult?> LoadAsync(string token, Guid id, CancellationToken cancellationToken)
    {
        if (await OpenAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        if (await cancellation.ChoicesAsync(View.Session, id, View.Locale, cancellationToken) is not { } choices)
        {
            return NotFound();
        }

        Choices = choices;
        return null;
    }
}
