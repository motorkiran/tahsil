using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;
using Tahsil.Domain.Portal;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Pages.Portal;

/// <summary>
/// The customer portal's overview (PRT §2): the customer's subscriptions with what their next renewal bills, saved
/// cards, invoices and billing details, each when its feature is on.
/// </summary>
public sealed class IndexModel(
    PortalPages pages,
    PortalCancellation cancellation,
    RequestThrottle throttle,
    IOptions<MerchantOptions> merchant,
    IOptions<BillingOptions> billing,
    IStringLocalizer<SharedResource> localizer)
    : PortalPageModel(pages, throttle, merchant, billing)
{
    /// <summary>What a finished task says on the way back to the overview.</summary>
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, string? done, CancellationToken cancellationToken)
    {
        if (await OpenAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        // A session opened for one task starts on it (PRT-2); the overview is a click, or a finished task, away.
        if (done is null && TaskOf(View) is { } task)
        {
            return Redirect(task);
        }

        Notice = done switch
        {
            "plan" => localizer["Your plan change is confirmed."].Value,
            "default" => localizer["Your default card was changed."].Value,
            "cancel_scheduled" => localizer["Your subscription will end as you chose. We sent you a confirmation."].Value,
            "canceled" => localizer["Your subscription has ended. We sent you a confirmation."].Value,
            "kept" or "resumed" => localizer["Your subscription continues."].Value,
            "details" => localizer["Your billing details were saved."].Value,
            _ => null,
        };
        return Page();
    }

    /// <summary>Takes back a cancellation scheduled for the period's end (SUB-33).</summary>
    public async Task<IActionResult> OnPostResumeAsync(string token, Guid subscription, CancellationToken cancellationToken)
    {
        if (await OpenAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        return (await cancellation.ResumeAsync(View.Session, subscription, cancellationToken)).IsSuccess ? Redirect($"/portal/{token}?done=resumed") : NotFound();
    }

    private string? TaskOf(PortalView view) => view.Session switch
    {
        { Flow: PortalFlow.PaymentMethodUpdate } when view.Has(PortalFeatures.PaymentMethod) => $"/portal/{Token}/payment-method",
        { Flow: PortalFlow.SubscriptionUpdate, FlowSubscriptionId: { } subscription } when view.Has(PortalFeatures.PlanChange) => $"/portal/{Token}/subscriptions/{subscription}/change",
        { Flow: PortalFlow.SubscriptionCancel, FlowSubscriptionId: { } subscription } when view.Has(PortalFeatures.Cancel) => $"/portal/{Token}/subscriptions/{subscription}/cancel",
        _ => null,
    };
}
