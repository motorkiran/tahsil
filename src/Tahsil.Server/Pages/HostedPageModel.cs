using System.Globalization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Tahsil.Server.Pages;

/// <summary>
/// What the hosted pages of checkouts and invoices share: no caching, no referrer, no indexing (CHK-32, INV-28), in
/// their customer's language.
/// </summary>
public abstract class HostedPageModel : PageModel
{
    /// <summary>The page's language, which <see cref="Ui.HostedPageCultureProvider"/> set from the session or invoice unless the person picked one.</summary>
    protected static string Locale => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex";
    }
}
