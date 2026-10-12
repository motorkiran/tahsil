using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Portal;

namespace Tahsil.Server.Ui;

/// <summary>
/// The hosted pages of a checkout, an invoice or the portal speak their customer's language, unless the person picked another one
/// with <c>?locale=</c> or the language switch.
/// </summary>
internal sealed class HostedPageCultureProvider : RequestCultureProvider
{
    private static readonly PathString Checkout = new("/checkout");
    private static readonly PathString Invoices = new("/invoices");
    private static readonly PathString Portal = new("/portal");

    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var locale = await LocaleAsync(httpContext);
        return locale is not null && ServerOptions.SupportedLocales.Contains(locale) ? new ProviderCultureResult(locale) : null;
    }

    private static async Task<string?> LocaleAsync(HttpContext context)
    {
        // The error page answers for the page that failed, in that page's language.
        var path = context.Features.Get<IStatusCodeReExecuteFeature>() is { } failed ? new PathString(failed.OriginalPath) : context.Request.Path;
        if (path.StartsWithSegments(Checkout, out var checkout) && Token(checkout) is { } checkoutToken)
        {
            var pages = context.RequestServices.GetRequiredService<CheckoutPages>();
            return await pages.FindAsync(checkoutToken, context.RequestAborted) is { } session ? await pages.LocaleOfAsync(session, context.RequestAborted) : null;
        }

        if (path.StartsWithSegments(Invoices, out var invoice) && Token(invoice) is { } invoiceToken)
        {
            var pages = context.RequestServices.GetRequiredService<InvoicePages>();
            return await pages.FindAsync(invoiceToken, context.RequestAborted) is { } found ? pages.LocaleOf(found) : null;
        }

        if (path.StartsWithSegments(Portal, out var portal) && Token(portal) is { } portalToken)
        {
            var pages = context.RequestServices.GetRequiredService<PortalPages>();
            return await pages.FindAsync(portalToken, context.RequestAborted) is { } session ? await pages.LocaleOfAsync(session, context.RequestAborted) : null;
        }

        return null;
    }

    private static string? Token(PathString rest) => rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var token, ..] ? token : null;
}
