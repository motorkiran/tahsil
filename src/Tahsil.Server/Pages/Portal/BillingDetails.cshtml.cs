using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Pages.Portal;

/// <summary>The customer's billing details in the portal (PRT §2): name, tax ID and office, address and phone.</summary>
public sealed class BillingDetailsModel(
    PortalPages pages, PortalDetails details, RequestThrottle throttle, IOptions<MerchantOptions> merchant, IOptions<BillingOptions> billing, IStringLocalizer<SharedResource> localizer)
    : PortalPageModel(pages, throttle, merchant, billing)
{
    [BindProperty]
    public PortalBillingDetails Details { get; set; } = new(null, null, null, null, null, null, null);

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        Details = await details.ReadAsync(View.Session, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken cancellationToken)
    {
        if (await LoadAsync(token, cancellationToken) is { } answered)
        {
            return answered;
        }

        var updated = await details.UpdateAsync(View.Session, Details, cancellationToken);
        if (updated.IsSuccess)
        {
            return Redirect($"/portal/{Token}?done=details");
        }

        // The rules of customers name the field and the reason, such as a tax ID that is not valid (CUS-4).
        ErrorMessage = updated.Error.Field is null ? localizer["Your details could not be saved. Please try again."] : updated.Error.Message;
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }

    private async Task<IActionResult?> LoadAsync(string token, CancellationToken cancellationToken) =>
        await OpenAsync(token, cancellationToken) ?? (View.Has(PortalFeatures.BillingDetails) ? null : NotFound());
}
