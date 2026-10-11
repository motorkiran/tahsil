using System.Globalization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Tahsil.Server.Pages.Checkout;

/// <summary>What the checkout's pages share: no caching, no referrer, no indexing (CHK-32), in the session's language.</summary>
public abstract class CheckoutPageModel : PageModel
{
    /// <summary>The page's language, which <see cref="Ui.CheckoutCultureProvider"/> set from the session unless the person picked one.</summary>
    protected static string Locale => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex";
    }
}
