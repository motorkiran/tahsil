using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;

namespace Tahsil.Server.Hosting;

/// <summary>
/// The client's address and scheme behind the reverse proxies of <c>Tahsil:Server:TrustedProxies</c>, and only them
/// (OPS-2): every proxy hop that is trusted is unwound, the first untrusted one is the client.
/// </summary>
internal static class ForwardedProxies
{
    public static IApplicationBuilder UseTrustedProxies(this IApplicationBuilder app)
    {
        var proxies = app.ApplicationServices.GetRequiredService<IOptions<ServerOptions>>().Value.TrustedProxies;
        return proxies.Count == 0 ? app : app.UseForwardedHeaders(Options(proxies));
    }

    internal static ForwardedHeadersOptions Options(IEnumerable<string> proxies)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null,
        };

        // The defaults trust loopback; only what is configured is trusted.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in proxies)
        {
            if (IPAddress.TryParse(proxy, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(proxy));
            }
        }

        return options;
    }
}
