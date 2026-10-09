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
    /// Rejects plain HTTP. Only disable it for local development; behind a TLS-terminating proxy, enable
    /// forwarded headers (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>) instead. Live mode requires it.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary><see cref="TahsilMode.Test"/> until the installation is ready to take real payments.</summary>
    public TahsilMode Mode { get; set; } = TahsilMode.Test;

    /// <summary>The locale of customers that have none, <c>tr</c> or <c>en</c>.</summary>
    public string DefaultLocale { get; set; } = "tr";

    public bool IsTestMode => Mode == TahsilMode.Test;

    internal bool HasValidPublicUrl() =>
        PublicUrl is { IsAbsoluteUri: true, Query: "", Fragment: "", UserInfo: "" }
        && (PublicUrl.Scheme == Uri.UriSchemeHttps || PublicUrl.Scheme == Uri.UriSchemeHttp);

    internal bool UsesHttpsWhenRequired() =>
        !RequireHttps || PublicUrl is null || PublicUrl.Scheme == Uri.UriSchemeHttps;

    internal bool RequiresHttpsInLiveMode() => Mode != TahsilMode.Live || RequireHttps;

    internal bool HasSupportedLocale() => SupportedLocales.Contains(DefaultLocale);
}
