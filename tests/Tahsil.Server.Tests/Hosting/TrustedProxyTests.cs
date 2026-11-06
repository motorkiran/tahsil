using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Tahsil.Server.Tests.Hosting;

/// <summary>The client's address and scheme behind reverse proxies, from the trusted ones only (OPS-2).</summary>
[Trait("Requirement", "OPS-2")]
public sealed class TrustedProxyTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", "203.0.113.7", "https")]
    [InlineData("10.0.0.0/8", "10.20.30.40", "203.0.113.7", "https")]
    [InlineData("10.0.0.5", "192.0.2.9", "192.0.2.9", "http")]
    [InlineData("", "10.0.0.5", "10.0.0.5", "http")]
    public async Task ForwardedHeaders_AreBelieved_OnlyFromTrustedProxies(string trusted, string peer, string client, string scheme)
    {
        await using var host = server.WithSettings(
            trusted.Length == 0 ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Tahsil:Server:TrustedProxies:0"] = trusted },
            services => services.AddSingleton<IStartupFilter>(new PeerAddress(IPAddress.Parse(peer))),
            endpoints => endpoints.MapGet("/test/client", (HttpContext context) => $"{context.Connection.RemoteIpAddress} {context.Request.Scheme}"));
        using var http = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/client");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await http.SendAsync(request, CancellationToken);

        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe($"{client} {scheme}");
    }

    [Fact]
    public void AProxyThatIsNotAnAddressOrNetwork_StopsStartup()
    {
        using var host = server.WithSettings(new Dictionary<string, string?> { ["Tahsil:Server:TrustedProxies:0"] = "proxy.internal" });

        Should.Throw<Exception>(() => host.CreateClient()).ToString().ShouldContain("TrustedProxies takes IP addresses and networks");
    }

    /// <summary>Gives every request the address of the peer that connected, as a proxy's connection has.</summary>
    private sealed class PeerAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
