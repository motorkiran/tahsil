using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;
using Tahsil.Domain.Portal;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Pages.Portal;

/// <summary>
/// A plan change in the portal (PRT §3): the prices on offer, then a preview of what the change charges or credits now
/// and of the next renewal, then the confirmation whose button names the obligation.
/// </summary>
public sealed class PlanModel(
    PortalPages pages, PortalPlans plans, RequestThrottle throttle, IOptions<MerchantOptions> merchant, IOptions<BillingOptions> billing, IStringLocalizer<SharedResource> localizer)
    : PortalPageModel(pages, throttle, merchant, billing)
{
    public PlanChoices Choices { get; private set; } = null!;

    /// <summary>The change being confirmed, once the customer chose one.</summary>
    public PlanChangeQuote? Quote { get; private set; }

    public string? ErrorMessage { get; private set; }

    [BindProperty]
    public Guid Price { get; set; }

    [BindProperty]
    public int Quantity { get; set; }

    public async Task<IActionResult> OnGetAsync(string token, Guid id, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, id, cancellationToken) is { } answered)
        {
            return answered;
        }

        Price = Choices.Offers.First(offer => offer.IsCurrent).PriceId;
        Quantity = Choices.Quantity;
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(string token, Guid id, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, id, cancellationToken) is { } answered)
        {
            return answered;
        }

        var quote = await plans.PreviewAsync(View.Session, id, Price, Quantity, View.Locale, cancellationToken);
        if (quote.IsFailure)
        {
            return Refused(quote.Error);
        }

        Quote = quote.Value;
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(string token, Guid id, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, id, cancellationToken) is { } answered)
        {
            return answered;
        }

        var changed = await plans.ConfirmAsync(
            View.Session, id, Price, Quantity, View.Locale, HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString(), cancellationToken);
        if (changed.IsFailure)
        {
            return Refused(changed.Error);
        }

        // An invoice the card could not pay is paid on its own page (PRT-6).
        return changed.Value.PayUrl is { } payUrl ? Redirect(payUrl.AbsoluteUri) : Done(PortalFlow.SubscriptionUpdate, "plan");
    }

    private async Task<IActionResult?> LoadAsync(string token, Guid id, CancellationToken cancellationToken)
    {
        if (await OpenAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        if (await plans.ChoicesAsync(View.Session, id, View.Locale, cancellationToken) is not { } choices)
        {
            return NotFound();
        }

        Choices = choices;
        return null;
    }

    private PageResult Refused(Domain.Common.Error error)
    {
        ErrorMessage = error.Code switch
        {
            "portal.nothing_changes" => localizer["This is your current plan."],
            "portal.plan_not_offered" => localizer["This plan or quantity is not offered."],
            _ => localizer["The plan could not be changed. Please try again."],
        };
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }
}
