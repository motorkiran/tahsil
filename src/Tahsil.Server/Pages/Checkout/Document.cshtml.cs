using Microsoft.AspNetCore.Mvc;
using Tahsil.Application.Checkout;
using Tahsil.Domain.Customers;

namespace Tahsil.Server.Pages.Checkout;

/// <summary>A legal document of the checkout in full (CHK-11), in its own tab.</summary>
public sealed class DocumentModel(CheckoutPages pages) : HostedPageModel
{
    public string Html { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(string token, string key, CancellationToken cancellationToken)
    {
        if (await pages.FindAsync(token, cancellationToken) is not { } session || await pages.ViewAsync(session, Locale, cancellationToken) is not { } view)
        {
            return NotFound();
        }

        var customer = view.Customer;
        var html = await pages.RenderDocumentAsync(
            session,
            key,
            view.Locale,
            new LegalDocumentCustomer(customer?.Name ?? session.CustomerDetails?.Name, customer?.Email ?? session.CustomerDetails?.Email, customer?.Type ?? CustomerType.Individual, null, null, null, null),
            cancellationToken);
        if (html is null)
        {
            return NotFound();
        }

        Html = html;
        return Page();
    }
}
