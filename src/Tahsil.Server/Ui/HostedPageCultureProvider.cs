using Microsoft.AspNetCore.Localization;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;

namespace Tahsil.Server.Ui;

/// <summary>
/// The hosted pages of a checkout or an invoice speak their customer's language, unless the person picked another one
/// with <c>?locale=</c> or the language switch.
/// </summary>
internal sealed class HostedPageCultureProvider : RequestCultureProvider
{
    private static readonly PathString Checkout = new("/checkout");
    private static readonly PathString Invoices = new("/invoices");

    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var locale = await LocaleAsync(httpContext);
        return locale is not null && ServerOptions.SupportedLocales.Contains(locale) ? new ProviderCultureResult(locale) : null;
    }

    private static async Task<string?> LocaleAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(Checkout, out var checkout) && Token(checkout) is { } checkoutToken)
        {
            var pages = context.RequestServices.GetRequiredService<CheckoutPages>();
            return await pages.FindAsync(checkoutToken, context.RequestAborted) is { } session ? await pages.LocaleOfAsync(session, context.RequestAborted) : null;
        }

        if (context.Request.Path.StartsWithSegments(Invoices, out var invoice) && Token(invoice) is { } invoiceToken)
        {
            var pages = context.RequestServices.GetRequiredService<InvoicePages>();
            return await pages.FindAsync(invoiceToken, context.RequestAborted) is { } found ? pages.LocaleOf(found) : null;
        }

        return null;
    }

    private static string? Token(PathString rest) => rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var token, ..] ? token : null;
}
