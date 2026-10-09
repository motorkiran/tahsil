using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Tahsil.Server.Ui;

/// <summary>
/// Sources a page adds to its own content security policy, such as a provider's card fields: scripts, frames,
/// connections, images and form targets. Other pages keep the base policy.
/// </summary>
public sealed record CspSources
{
    public IReadOnlyList<string> Scripts { get; init; } = [];

    public IReadOnlyList<string> Frames { get; init; } = [];

    public IReadOnlyList<string> Connections { get; init; } = [];

    public IReadOnlyList<string> Images { get; init; } = [];

    public IReadOnlyList<string> FormActions { get; init; } = [];
}

/// <summary>Implemented by page models whose page needs more than the base policy allows.</summary>
public interface IContentSecurityPolicySources
{
    CspSources GetCspSources();
}

/// <summary>Marks a page that runs Tahsil's own scripts, loaded with the request's nonce.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class RunsScriptsAttribute : Attribute;

internal static class ContentSecurityPolicy
{
    private const string NonceItemKey = "Tahsil.CspNonce";

    /// <summary>The nonce that allows this request's inline style and own scripts under the content security policy.</summary>
    public static string GetCspNonce(this HttpContext context)
    {
        if (context.Items[NonceItemKey] is not string nonce)
        {
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceItemKey] = nonce;
        }

        return nonce;
    }

    /// <summary>
    /// The policy of a page: nothing but Tahsil's own resources, no plugins, no framing; scripts only when the page
    /// runs Tahsil's scripts or adds a provider's; plus whatever the page adds.
    /// </summary>
    public static string Build(string nonce, bool runsScripts, CspSources? sources = null)
    {
        sources ??= new CspSources();
        var scripts = runsScripts || sources.Scripts.Count > 0 ? Join($"'nonce-{nonce}'", sources.Scripts) : "'none'";

        return string.Join("; ",
        [
            "default-src 'self'",
            $"script-src {scripts}",
            $"style-src 'self' 'nonce-{nonce}'",
            $"img-src {Join("'self' data: https:", sources.Images)}",
            $"frame-src {Join("'self'", sources.Frames)}",
            $"connect-src {Join("'self'", sources.Connections)}",
            $"form-action {Join("'self'", sources.FormActions)}",
            "object-src 'none'",
            "base-uri 'self'",
            "frame-ancestors 'none'",
        ]);
    }

    private static string Join(string baseSources, IReadOnlyList<string> extra) =>
        extra.Count == 0 ? baseSources : $"{baseSources} {string.Join(' ', extra.Distinct(StringComparer.Ordinal))}";

    /// <summary>Adds the policy to every page Tahsil renders.</summary>
    internal sealed class Filter : IAsyncResultFilter
    {
        public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            if (context.Result is PageResult)
            {
                var runsScripts = context.ActionDescriptor.EndpointMetadata.OfType<RunsScriptsAttribute>().Any();
                var sources = (context.Controller as IContentSecurityPolicySources)?.GetCspSources();
                context.HttpContext.Response.Headers.ContentSecurityPolicy = Build(context.HttpContext.GetCspNonce(), runsScripts, sources);
            }

            return next();
        }
    }
}
