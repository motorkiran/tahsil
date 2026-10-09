using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;
using Tahsil.Application.Common;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Ui;

/// <summary>The hosted pages (checkout, invoice, portal): Razor Pages in Turkish and English.</summary>
internal static class HostedPages
{
    public const string ErrorPath = "/error";

    /// <summary>The query parameter that picks the language of a page, as in <c>?locale=tr</c>.</summary>
    public const string LocaleParameter = "locale";

    public static readonly CultureInfo[] SupportedCultures = [new("tr"), new("en")];

    /// <summary>Paths that machines call; everything else is a page that people see.</summary>
    private static readonly PathString[] MachinePaths = ["/api", "/openapi", "/scalar", "/health", "/notifications"];

    /// <summary>
    /// Whether the request is for a page people see. While an exception is handled, the request's path is already the
    /// error page's, so the original one is used.
    /// </summary>
    public static bool IsPageRequest(HttpContext context)
    {
        var path = context.Features.Get<IExceptionHandlerPathFeature>()?.Path is { } original ? new PathString(original) : context.Request.Path;
        return !Array.Exists(MachinePaths, machinePath => path.StartsWithSegments(machinePath, StringComparison.OrdinalIgnoreCase));
    }

    public static IServiceCollection AddHostedPages(this IServiceCollection services)
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddSingleton<BrandingStylesheet>();

        services.AddOptions<RateLimitOptions>()
            .BindConfiguration(RateLimitOptions.SectionName)
            .Validate(options => options.IsValid(), $"{RateLimitOptions.SectionName}: limits must be positive.")
            .ValidateOnStart();
        services.AddSingleton<RequestThrottle>();

        // Write Turkish and other non-Latin text as UTF-8 instead of numeric entities; characters that are special
        // in HTML are still encoded.
        services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

        services.AddRazorPages(options => options.Conventions.ConfigureFilter(new ContentSecurityPolicy.Filter()))
            .AddViewLocalization()
            .AddDataAnnotationsLocalization(options =>
                options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

        services.AddSingleton<IConfigureOptions<RequestLocalizationOptions>, ConfigureLocalization>();

        return services;
    }

    /// <summary>
    /// Errors of pages people see become the localized error page; the API and other machine endpoints keep their
    /// problem details. The re-executing middleware must sit on the application itself, not in a branch: only there
    /// can it run routing again to reach the error page.
    /// </summary>
    public static IApplicationBuilder UseErrorResponses(this IApplicationBuilder app)
    {
        // The exception handler answers machine requests itself and leaves pages to the error page.
        app.UseExceptionHandler(ErrorPath);
        app.UseStatusCodePagesWithReExecute(ErrorPath, "?status={0}");
        app.Use((context, next) =>
        {
            if (!IsPageRequest(context) && context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
            {
                statusCodePages.Enabled = false;
            }

            return next(context);
        });
        app.UseWhen(context => !IsPageRequest(context), machine => machine.UseStatusCodePages());
        return app;
    }

    /// <summary>Serves the custom stylesheet of the hosted pages, when there is one.</summary>
    public static IEndpointRouteBuilder MapBrandingStylesheet(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(BrandingStylesheet.Path, (BrandingStylesheet stylesheet, HttpContext context) =>
        {
            if (stylesheet.Content is null)
            {
                return Results.NotFound();
            }

            // The pages link it under a URL that changes with its content.
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Text(stylesheet.Content, "text/css", Encoding.UTF8);
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>Remembers the language a person picks in the page footer.</summary>
    public static IEndpointRouteBuilder MapCultureSwitch(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/culture/{culture}", (string culture, string? returnUrl, HttpContext context) =>
        {
            if (Array.Exists(SupportedCultures, supported => supported.Name == culture))
            {
                context.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365) });
            }

            return Results.LocalRedirect(LocalUrls.IsLocal(returnUrl) ? returnUrl! : "/");
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// The language of a page: the <c>locale</c> parameter, then the person's choice (cookie), then the browser's,
    /// then <c>Tahsil:Server:DefaultLocale</c>. Pages of a customer's session use the customer's locale instead.
    /// </summary>
    private sealed class ConfigureLocalization(IOptions<ServerOptions> server) : IConfigureOptions<RequestLocalizationOptions>
    {
        public void Configure(RequestLocalizationOptions options)
        {
            options.DefaultRequestCulture = new RequestCulture(server.Value.DefaultLocale);
            options.SupportedCultures = SupportedCultures;
            options.SupportedUICultures = SupportedCultures;
            options.RequestCultureProviders =
            [
                new QueryStringRequestCultureProvider { QueryStringKey = LocaleParameter, UIQueryStringKey = LocaleParameter },
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider(),
            ];
        }
    }
}
