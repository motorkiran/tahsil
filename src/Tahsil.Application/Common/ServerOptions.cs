using System.Net;

namespace Tahsil.Application.Common;

/// <summary>Host settings from the <c>Tahsil:Server</c> configuration section.</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Tahsil:Server";

    /// <summary>The locales of hosted pages, emails and documents.</summary>
    public static readonly IReadOnlyList<string> SupportedLocales = ["tr", "en"];

    /// <summary>
    /// The public base URL of this installation, such as <c>https://billing.example.com/</c>. Every link Tahsil
    /// produces (hosted pages, emails, provider notification and return URLs) starts with it, so it is never
    /// inferred from request headers.
    /// </summary>
    public Uri? PublicUrl { get; set; }

    /// <summary>
    /// Requires HTTPS: the public URL must use it, and browsers are told to use nothing else (HSTS). Only disable it
    /// for local development; behind a TLS-terminating proxy, list the proxy in <see cref="TrustedProxies"/> instead.
    /// Live mode requires it.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary><see cref="TahsilMode.Test"/> until the installation is ready to take real payments.</summary>
    public TahsilMode Mode { get; set; } = TahsilMode.Test;

    /// <summary>The locale of customers that have none, <c>tr</c> or <c>en</c>.</summary>
    public string DefaultLocale { get; set; } = "tr";

    /// <summary>
    /// The reverse proxies in front of Tahsil, as addresses (<c>10.0.0.5</c>) or networks (<c>10.0.0.0/8</c>). From them
    /// only, <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> give the client's address and the scheme it used. List
    /// only proxies you run: the client's address feeds rate limits, consent records and provider requests.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    public bool IsTestMode => Mode == TahsilMode.Test;

    internal bool HasValidPublicUrl() =>
        PublicUrl is { IsAbsoluteUri: true, Query: "", Fragment: "", UserInfo: "" }
        && (PublicUrl.Scheme == Uri.UriSchemeHttps || PublicUrl.Scheme == Uri.UriSchemeHttp);

    internal bool UsesHttpsWhenRequired() =>
        !RequireHttps || PublicUrl is null || PublicUrl.Scheme == Uri.UriSchemeHttps;

    internal bool RequiresHttpsInLiveMode() => Mode != TahsilMode.Live || RequireHttps;

    internal bool HasSupportedLocale() => SupportedLocales.Contains(DefaultLocale);

    internal bool HasValidTrustedProxies() =>
        TrustedProxies.TrueForAll(proxy => IPAddress.TryParse(proxy, out _) || IPNetwork.TryParse(proxy, out _));
}
