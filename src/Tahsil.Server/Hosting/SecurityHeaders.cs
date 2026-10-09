namespace Tahsil.Server.Hosting;

internal static class SecurityHeaders
{
    /// <summary>Headers that are safe for every response.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        // Hosted page URLs carry session tokens; never leak them to other sites.
        headers["Referrer-Policy"] = "no-referrer";
        return next(context);
    });
}
