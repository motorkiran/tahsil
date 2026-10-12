using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Pages.Portal;

/// <summary>
/// The customer portal's overview (PRT §2): the customer's subscriptions with what their next renewal bills, saved
/// cards, invoices and billing details, each when its feature is on.
/// </summary>
public sealed class IndexModel(PortalPages pages, RequestThrottle throttle, IOptions<MerchantOptions> merchant, IOptions<BillingOptions> billing)
    : PortalPageModel(pages, throttle, merchant, billing)
{
    public async Task<IActionResult> OnGetAsync(string token, CancellationToken cancellationToken) =>
        await OpenAsync(token, cancellationToken) ?? Page();
}
