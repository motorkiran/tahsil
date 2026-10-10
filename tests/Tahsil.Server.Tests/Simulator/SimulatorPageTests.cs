using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Server.Tests.Simulator;

/// <summary>The simulator's card page, served by Tahsil, from start to the customer's return.</summary>
public sealed partial class SimulatorPageTests(TahsilServerFixture server)
{
    private static readonly Uri ReturnUrl = new("https://shop.example.com/return");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SIM-3")]
    [Trait("Requirement", "SIM-6")]
    public async Task ACustomerPaysOnThePage_AndCompleteReadsTheResult()
    {
        await using var host = Host();
        var (call, page) = await StartAsync(host, "pay-1");
        using var client = host.CreateClient();

        using var form = await client.GetAsync(page, CancellationToken);
        var finished = await PostAsync(client, page, form, "Pay", new() { ["number"] = "4242 4242 4242 4242", ["expiry"] = "12/30", ["cvc"] = "123" });
        var outcome = await call.Provider.CompleteAsync(call.Context, Complete("pay-1"), CancellationToken);

        form.StatusCode.ShouldBe(HttpStatusCode.OK);
        form.Headers.GetValues("X-Frame-Options").ShouldBe(["SAMEORIGIN"]);
        form.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'self'");
        finished.QuerySelector<IHtmlAnchorElement>("#continue")!.Href.ShouldBe(ReturnUrl.AbsoluteUri);
        finished.QuerySelector<IHtmlAnchorElement>("#continue")!.Target.ShouldBe("_top");
        (outcome.Status, outcome.ChargedAmount).ShouldBe((PaymentStatus.Succeeded, new Amount(12_500, "TRY", 2)));
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.simulator_transactions WHERE kind = 'payment'")).ShouldBe([1]);
    }

    [Fact]
    [Trait("Requirement", "SIM-5")]
    public async Task ThreeDSecureCards_GoThroughTheSimulatedBank()
    {
        await using var host = Host();
        var (call, page) = await StartAsync(host, "pay-1");
        using var client = host.CreateClient();

        using var form = await client.GetAsync(page, CancellationToken);
        var bank = await PostAsync(client, page, form, "Pay", new() { ["number"] = "4000000000003220", ["expiry"] = "12/30", ["cvc"] = "123" });
        using var bankPage = await client.GetAsync(page, CancellationToken);
        var finished = await PostAsync(client, page, bankPage, "Authenticate", new() { ["decision"] = "Approve" });
        var outcome = await call.Provider.CompleteAsync(call.Context, Complete("pay-1"), CancellationToken);

        bank.QuerySelector("#approve").ShouldNotBeNull();
        bank.QuerySelector("h2")!.TextContent.ShouldBe("Simüle edilmiş banka");
        finished.QuerySelector("#continue").ShouldNotBeNull();
        (outcome.Status, outcome.ThreeDSecure).ShouldBe((PaymentStatus.Succeeded, ThreeDSecureResult.Authenticated));
    }

    [Fact]
    [Trait("Requirement", "SIM-4")]
    public async Task OtherCardNumbers_AreRefused_WithoutBeingStoredOrLogged()
    {
        const string RealLooking = "4111111111111111";
        var logs = new CapturingLoggerProvider();
        await using var host = Host(services => services.AddSingleton<ILoggerProvider>(logs));
        var (_, page) = await StartAsync(host, "pay-1");
        using var client = host.CreateClient();

        using var form = await client.GetAsync(page, CancellationToken);
        var refused = await PostAsync(client, page, form, "Pay", new() { ["number"] = RealLooking, ["expiry"] = "12/30", ["cvc"] = "123" });

        refused.QuerySelector("#card-error")!.TextContent.ShouldBe("Yalnızca simülatör test kartları kabul edilir.");
        refused.QuerySelector<IHtmlInputElement>("#number")!.Value.ShouldBeEmpty();
        refused.DocumentElement.OuterHtml.ShouldNotContain(RealLooking);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.simulator_transactions WHERE state::text LIKE {"%" + RealLooking + "%"}")).ShouldBe([0]);
        logs.Entries.ShouldAllBe(entry => !entry.Message.Contains(RealLooking) && (entry.Exception == null || !entry.Exception.ToString().Contains(RealLooking)));
    }

    [Fact]
    [Trait("Requirement", "SIM-3")]
    public async Task UnknownTokens_AreNotFound()
    {
        await using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/simulator/pay/no-such-token", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("Requirement", "SIM-1")]
    [InlineData("Test", true)]
    [InlineData("Live", false)]
    public void ThePageAndTheProvider_ExistInTestModeOnly(string mode, bool exists)
    {
        var settings = new Dictionary<string, string?> { ["Tahsil:Server:Mode"] = mode };
        if (mode == "Live")
        {
            settings["Tahsil:Server:PublicUrl"] = "https://billing.example.com/";
            settings["Tahsil:Server:RequireHttps"] = "true";
        }

        using var host = new TestHost(settings);
        host.CreateClient().Dispose();

        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText);
        routes.Any(route => route?.TrimStart('/') == "simulator/pay/{token}").ShouldBe(exists);
        (host.Services.GetRequiredService<ProviderRegistry>().Find("simulator") is not null).ShouldBe(exists);
    }

    private WebApplicationFactory<Program> Host(Action<IServiceCollection>? services = null) =>
        server.WithSettings(new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = server.NewDatabase("simulator") }, services);

    /// <summary>Starts a payment on the simulator connection a fresh installation has, and returns its page.</summary>
    private static async Task<(ProviderCall Call, Uri Page)> StartAsync(WebApplicationFactory<Program> host, string reference)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var connection = await scope.ServiceProvider.GetRequiredService<ITahsilDbContext>().ProviderConnections.SingleAsync(row => row.Key == "simulator", CancellationToken);
        var call = host.Services.GetRequiredService<ProviderAccess>().For(connection)!;
        var started = await call.Provider.StartAsync(call.Context, new StartRequest
        {
            PaymentId = Guid.CreateVersion7(),
            Reference = reference,
            Amount = new Amount(12_500, "TRY", 2),
            Purpose = PaymentPurpose.Charge,
            Customer = new CustomerInfo { Id = Guid.CreateVersion7() },
            ReturnUrl = ReturnUrl,
            CancelUrl = new Uri("https://shop.example.com/cancel"),
        }, CancellationToken);

        var source = FrameSource().Match(started.Presentation.ShouldBeOfType<Presentation.Embed>().Html).Groups["source"].Value;
        return (call, new Uri(new Uri(source).PathAndQuery, UriKind.Relative));
    }

    private static CompleteRequest Complete(string reference) =>
        new() { PaymentId = Guid.CreateVersion7(), Reference = reference, Amount = new Amount(12_500, "TRY", 2) };

    /// <summary>Posts a form of the page with its antiforgery token, as the browser would.</summary>
    private static async Task<IHtmlDocument> PostAsync(HttpClient client, Uri page, HttpResponseMessage current, string handler, Dictionary<string, string> fields)
    {
        var document = new HtmlParser().ParseDocument(await current.Content.ReadAsStringAsync(CancellationToken));
        fields["__RequestVerificationToken"] = document.QuerySelector<IHtmlInputElement>("input[name=__RequestVerificationToken]")!.Value;
        using var content = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(new Uri($"{page}?handler={handler}", UriKind.Relative), content, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(CancellationToken));
    }

    [GeneratedRegex("src=\"(?<source>[^\"]+)\"")]
    private static partial Regex FrameSource();
}
