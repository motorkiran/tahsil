using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Notifications;
using Tahsil.Contracts;
using Tahsil.Server.Tests.Notifications;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Portal;

/// <summary>
/// Portal sessions and the portal's overview (PRT §1, §2, §7), from April 1, 2026: a customer with a monthly Team
/// subscription paid by card sees their own billing, and no one else's.
/// </summary>
public sealed partial class PortalSessionTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);
    private readonly CapturingEmailSender _email = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PRT-1")]
    [Trait("Requirement", "PRT-12")]
    public async Task ASession_OpensTheCustomersBilling_AsOftenAsTheyLike_UntilItExpires()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (customer, subscription) = await SubscriberAsync(api, "Ada Lovelace", "ada@example.com", "4242424242424242");
        await host.RunWorkersUntilIdleAsync();
        using var browser = Open(host);

        var session = await SessionAsync(api, new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "en" });
        using var first = await browser.GetAsync(session.Url, CancellationToken);
        var page = await ParseAsync(first);
        using var again = await browser.GetAsync(session.Url, CancellationToken);
        using var unknown = await browser.GetAsync(new Uri(session.Url.AbsoluteUri[..^4] + "AAAA"), CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(61));
        using var late = await browser.GetAsync(session.Url, CancellationToken);
        var expired = await ParseAsync(late);

        session.Url.AbsolutePath.ShouldStartWith("/portal/ps_");
        (session.ExpiresAt, session.CustomerId).ShouldBe((April.AddHours(1), customer));
        (first.StatusCode, again.StatusCode, unknown.StatusCode, late.StatusCode).ShouldBe((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.NotFound));
        (first.Headers.CacheControl?.NoStore, first.Headers.GetValues("Referrer-Policy").Single(), first.Headers.GetValues("X-Robots-Tag").Single()).ShouldBe((true, "no-referrer", "noindex"));
        page.QuerySelector("#subscriptions .plan")!.TextContent.ShouldBe("Team");
        page.QuerySelector("#subscriptions .renewal")!.TextContent.ShouldBe("Renews on May 1, 2026 for ₺300.00, taxes included.");
        page.QuerySelector("#payment-methods .card-row")!.TextContent.ShouldContain("visa •••• 4242");
        page.QuerySelector("#payment-methods .card-row")!.TextContent.ShouldContain("Default");
        var invoice = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{subscription.LatestInvoice!.Id}");
        page.QuerySelector("#invoices .invoice-row a")!.TextContent.ShouldBe(invoice.Number);
        page.QuerySelector("#invoices .invoice-row a")!.GetAttribute("href").ShouldBe(invoice.HostedInvoiceUrl!.AbsoluteUri);
        page.QuerySelector("#billing-details")!.TextContent.ShouldContain("ada@example.com");
        page.QuerySelector("#return")!.GetAttribute("href").ShouldBe("https://app.example.com/billing");
        page.QuerySelector("#merchant")!.TextContent.ShouldContain("MERSİS");
        expired.QuerySelector("h1")!.TextContent.ShouldBe("Page not found");
        expired.QuerySelector("#return")!.GetAttribute("href").ShouldBe("https://app.example.com/billing");
        expired.QuerySelector("#subscriptions").ShouldBeNull();
    }

    [Fact]
    [Trait("Requirement", "PRT-3")]
    public async Task ASession_NeverShowsAnotherCustomersData()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (ada, _) = await SubscriberAsync(api, "Ada Lovelace", "ada@example.com", "4242424242424242");
        var (grace, hers) = await SubscriberAsync(api, "Grace Hopper", "grace@example.com", "4000000000007726", "starter-monthly-try");
        var herInvoice = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{hers.LatestInvoice!.Id}");
        using var browser = Open(host);

        var session = await SessionAsync(api, new CreatePortalSessionRequest { Customer = ada, ReturnUrl = "https://app.example.com/billing", Locale = "en" });
        var page = await (await browser.GetAsync(session.Url, CancellationToken)).Content.ReadAsStringAsync(CancellationToken);

        page.ShouldContain("ada@example.com");
        foreach (var theirs in new[] { "Grace", "grace@example.com", "7726", herInvoice.Number!, "Growth", grace.ToString(), hers.Id.ToString() })
        {
            page.ShouldNotContain(theirs);
        }
    }

    [Theory]
    [Trait("Requirement", "PRT-1")]
    [Trait("Requirement", "PRT-2")]
    [InlineData("http://app.example.com/billing", null, false, HttpStatusCode.BadRequest, "portal.invalid_url")]
    [InlineData("https://app.example.com/billing", PortalFlow.SubscriptionCancel, false, HttpStatusCode.BadRequest, "portal.invalid")]
    [InlineData("https://app.example.com/billing", PortalFlow.PaymentMethodUpdate, true, HttpStatusCode.BadRequest, "portal.invalid")]
    [InlineData("https://app.example.com/billing", PortalFlow.SubscriptionUpdate, true, HttpStatusCode.Created, null)]
    public async Task ASession_NeedsAReturnUrl_AndASubscriptionForItsSubscriptionFlows(string returnUrl, PortalFlow? flow, bool withSubscription, HttpStatusCode status, string? code)
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (ada, subscription) = await SubscriberAsync(api, "Ada Lovelace", "ada@example.com", "4242424242424242");
        var (grace, _) = await SubscriberAsync(api, "Grace Hopper", "grace@example.com", "4000000000007726");

        using var response = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest
        {
            Customer = ada,
            ReturnUrl = returnUrl,
            Flow = flow,
            FlowSubscription = withSubscription ? subscription.Id : null,
        });
        using var notTheirs = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest
        {
            Customer = grace,
            ReturnUrl = "https://app.example.com/billing",
            Flow = PortalFlow.SubscriptionCancel,
            FlowSubscription = subscription.Id,
        });
        using var noOne = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest { Customer = Guid.CreateVersion7(), ReturnUrl = returnUrl });

        (response.StatusCode, code is null ? null : await response.ProblemCodeAsync()).ShouldBe((status, code));
        (notTheirs.StatusCode, await notTheirs.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "portal.invalid"));
        (noOne.StatusCode, await noOne.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "customer.not_found"));
    }

    [Fact]
    [Trait("Requirement", "PRT-12")]
    public async Task AFeatureTurnedOff_IsNotOnThePage_AndAnUnknownOneStopsTahsil()
    {
        await using var host = Host(new() { ["Tahsil:Portal:Features:invoices"] = "false", ["Tahsil:Portal:Features:payment_method"] = "false" });
        using var api = await ClientAsync(host);
        var (customer, _) = await SubscriberAsync(api, "Ada Lovelace", "ada@example.com", "4242424242424242");
        using var browser = Open(host);
        var session = await SessionAsync(api, new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "tr" });

        var page = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        await using var misnamed = Host(new() { ["Tahsil:Portal:Features:invoice"] = "false" });

        page.QuerySelector("h1")!.TextContent.ShouldBe("Faturalandırma");
        page.QuerySelector("#subscriptions .renewal")!.TextContent.ShouldBe("1 Mayıs 2026 tarihinde, vergiler dahil ₺300,00 ile yenilenir.");
        (page.QuerySelector("#invoices"), page.QuerySelector("#payment-methods")).ShouldBe((null, null));
        page.QuerySelector("#billing-details").ShouldNotBeNull();
        Should.Throw<Exception>(() => misnamed.CreateClient()).ToString().ShouldContain("Tahsil:Portal:Features");
    }

    [Fact]
    [Trait("Requirement", "PRT-4")]
    public async Task AFailedPaymentsEmail_LinksIntoThePortal_ForAWeek()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var customer = await Payments.PaymentScenarios.CustomerAsync(api);
        var card = await SaveCardAsync(api, customer, "4242424242424242");
        await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        await SetBehaviorAsync(api, card, "insufficient_funds");
        foreach (var instant in new[] { April.AddMonths(1), April.AddMonths(1).AddHours(1) })
        {
            _time.Advance(instant - _time.GetUtcNow());
            await host.RunWorkersUntilIdleAsync();
        }

        var failed = _email.Sent.Single(message => message.Subject.StartsWith("Ödemeniz alınamadı", StringComparison.Ordinal));
        var link = new Uri(PortalLink().Match(failed.Text).Value);
        using var browser = Open(host);
        _time.Advance(TimeSpan.FromDays(6));
        using var inTime = await browser.GetAsync(link, CancellationToken);
        var page = await ParseAsync(inTime);
        _time.Advance(TimeSpan.FromDays(2));
        using var late = await browser.GetAsync(link, CancellationToken);

        failed.Text.ShouldContain("Kartımı güncelle: ");
        inTime.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.QuerySelector("#subscriptions .status")!.TextContent.ShouldBe("Ödeme gecikti");
        page.QuerySelector("#return").ShouldBeNull("the email's session has no application to return to");
        late.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>A customer with a monthly subscription paid by a card of their own.</summary>
    private static async Task<(Guid Customer, SubscriptionResponse Subscription)> SubscriberAsync(HttpClient api, string name, string email, string card, string price = "team-monthly-try")
    {
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = name, Email = email, Locale = "en" });
        var customer = (await created.ReadAsync<CustomerResponse>()).Id;
        await SaveCardAsync(api, customer, card);
        return (customer, await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = price }] }));
    }

    private static async Task<PortalSessionResponse> SessionAsync(HttpClient api, CreatePortalSessionRequest request)
    {
        using var response = await api.PostJsonAsync("/api/v1/portal-sessions", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<PortalSessionResponse>();
    }

    [GeneratedRegex(@"https?://\S+/portal/ps_[A-Za-z0-9_-]+")]
    private static partial Regex PortalLink();

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("portal"), _time, services => services.AddSingleton<IEmailSender>(_email), settings);
}
