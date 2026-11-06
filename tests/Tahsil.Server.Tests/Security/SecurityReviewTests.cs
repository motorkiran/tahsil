using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Server.Diagnostics;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Security;

/// <summary>Findings of the M12 security review (SEC §4) that the other suites did not cover, kept fixed by tests.</summary>
/// <remarks>Alone: the trace test listens to every request of the process.</remarks>
[Collection(nameof(SecurityReviewTests))]
public sealed class SecurityReviewTests(TahsilServerFixture server)
{
    private const string TaxId = "10000000146";
    private const string Card = "4242424242424242";

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "SEC-15")]
    [InlineData("/checkout/cs_QT0oe6U2eW-xOTLTqSJGiRVHK6pxbF4ZgrVRfyznG3U", "/checkout/{token}")]
    [InlineData("/checkout/cs_QT0oe6U2eW-xOTLTqSJGiRVHK6pxbF4ZgrVRfyznG3U/return/01a1b0da-8fd2-7af3-9196-26bb6a62b535", "/checkout/{token}/return/01a1b0da-8fd2-7af3-9196-26bb6a62b535")]
    [InlineData("/portal/ps_Mx3K9aLq2Wv8Rt5Yu1Io0Pz7Xc4Vb6Nm/subscriptions/01a1b0da-8fd2-7af3-9196-26bb6a62b535/cancel", "/portal/{token}/subscriptions/01a1b0da-8fd2-7af3-9196-26bb6a62b535/cancel")]
    [InlineData("/invoices/uTehAf5YMHGt2KSmdc3uhTzPswRSzoB7eYu7XVmJX2srHRgMlZrNY5dhYhHUy_fq", "/invoices/{token}")]
    [InlineData("/simulator/pay/7a8fH1mImty0RQWDSxUEeWWZUFuYZEuRwC9o6JvV6ww", "/simulator/pay/{token}")]
    [InlineData("/provider-notifications/01a1b0da-8fd2-7af3-9196-26bb6a62b535", "/provider-notifications/01a1b0da-8fd2-7af3-9196-26bb6a62b535")]
    [InlineData("/api/v1/customers/by-external-id/user-42", "/api/v1/customers/by-external-id/user-42")]
    public void TracedPaths_KeepTheirIds_ButNotTheirTokens(string path, string traced) =>
        TelemetryExtensions.WithoutTokens(path).ShouldBe(traced);

    [Fact]
    [Trait("Requirement", "SEC-15")]
    public async Task ATracedRequest_ToAHostedPage_CarriesNoAccessToken()
    {
        var paths = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("url.path") is string path)
                {
                    paths.Enqueue(path);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = Host();
        using var api = await ClientAsync(host);
        var session = await CheckoutAsync(api);
        using var browser = Open(host);

        using var page = await browser.GetAsync(session.Url, CancellationToken);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = session.Url!.Segments[^1];

        // The server's activity stops once the response is written, which may be a moment after the client has it.
        var waited = Stopwatch.StartNew();
        while (!paths.Contains("/checkout/{token}") && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken);
        }

        paths.ShouldContain("/checkout/{token}");
        paths.ShouldAllBe(path => !path.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "SEC-15")]
    [Trait("Requirement", "SEC-9")]
    [Trait("Requirement", "ADP-2")]
    [Trait("Requirement", "SEC-1")]
    public async Task APurchaseAndThePortal_LogNoSecretTokenCardOrIdentityNumber()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("security_logs"), _time, services => services.AddSingleton<ILoggerProvider>(logs));
        var key = await host.CreateApiKeyAsync([.. Permissions, TahsilPermissions.WebhooksWrite]);
        using var api = host.CreateClient(key.Key);
        await ClientAsync(host);
        using var endpoint = await api.PostJsonAsync("/api/v1/webhooks/endpoints", new CreateWebhookEndpointRequest { Url = "https://app.example.com/webhooks" });
        var webhookSecret = (await endpoint.ReadAsync<CreatedWebhookEndpointResponse>()).Secret;
        var session = await CheckoutAsync(api);
        using var browser = Open(host);

        var form = Form("ada@example.com", "Ada Lovelace", ["pre-information", "distance-sales"]);
        form["Form.CardStorage"] = "true";
        using (var submitted = await SubmitAsync(browser, session.Url!, form))
        using (var returned = await PayOnTheEmbedAsync(host, browser, submitted, Card, saveCard: true))
        {
            returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var customer = (await GetAsync<CheckoutSessionResponse>(api, $"/api/v1/checkout-sessions/{session.Id}")).CustomerId!.Value;
        using (var updated = await api.PatchJsonAsync($"/api/v1/customers/{customer}", new { taxId = TaxId, type = "individual" }))
        {
            updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(CancellationToken));
        }

        using var portal = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing" });
        var portalUrl = (await portal.ReadAsync<PortalSessionResponse>()).Url;
        using (var opened = await browser.GetAsync(portalUrl, CancellationToken))
        {
            opened.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        _time.Advance(TimeSpan.FromDays(32));
        await host.RunWorkersUntilIdleAsync();

        string[] secrets = [key.Key, webhookSecret, Card, TaxId, session.Url!.Segments[^1], portalUrl.Segments[^1]];
        logs.Entries.ShouldNotBeEmpty();
        foreach (var secret in secrets)
        {
            logs.Entries.Where(entry => entry.Message.Contains(secret, StringComparison.Ordinal) || entry.Exception?.ToString().Contains(secret, StringComparison.Ordinal) == true)
                .Select(entry => $"{entry.Category}: {entry.Message}")
                .ShouldBeEmpty($"a log entry holds {secret[..4]}…");
        }
    }

    private static async Task<CheckoutSessionResponse> CheckoutAsync(HttpClient api)
    {
        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Subscription,
            Items = [new CheckoutItemRequest { Price = "team-monthly-try" }],
            CustomerDetails = new CheckoutCustomerDetails { Email = "ada@example.com", Locale = "en" },
            SuccessUrl = "https://app.example.com/done",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return await created.ReadAsync<CheckoutSessionResponse>();
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("security"), _time);
}

/// <summary>Runs <see cref="SecurityReviewTests"/> apart from every other test.</summary>
[CollectionDefinition(nameof(SecurityReviewTests), DisableParallelization = true)]
public sealed class RunsAlone;

