using Microsoft.AspNetCore.Localization;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;

namespace Tahsil.Server.Ui;

/// <summary>
/// The checkout's pages speak the session's language (its own, or its customer's), unless the person picked another
/// one with <c>?locale=</c> or the language switch.
/// </summary>
internal sealed class CheckoutCultureProvider : RequestCultureProvider
{
    private static readonly PathString Prefix = new("/checkout");

    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        if (!httpContext.Request.Path.StartsWithSegments(Prefix, out var rest) || rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) is not [var token, ..])
        {
            return null;
        }

        var pages = httpContext.RequestServices.GetRequiredService<CheckoutPages>();
        return await pages.FindAsync(token, httpContext.RequestAborted) is { } session && await pages.LocaleOfAsync(session, httpContext.RequestAborted) is { } locale
            && ServerOptions.SupportedLocales.Contains(locale)
            ? new ProviderCultureResult(locale)
            : null;
    }
}
