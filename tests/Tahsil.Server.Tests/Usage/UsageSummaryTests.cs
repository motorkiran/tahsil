using System.Net;
using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Admin.Components.Pages.Customers;
using Tahsil.Admin.Components.Pages.Subscriptions;
using Tahsil.Contracts;
using Tahsil.Server.Tests.Admin;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using static Tahsil.Server.Tests.Usage.UsageScenarios;

namespace Tahsil.Server.Tests.Usage;

/// <summary>Usage summaries (USG §3): through the API, in the admin panel and for the customer in the portal.</summary>
public sealed class UsageSummaryTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "USG-8")]
    public async Task ACustomersUsage_IsSummedPerDayAndMonth_OfTheBillingTimeZone()
    {
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Billing:TimeZone"] = "Europe/Istanbul" });
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var start = _time.GetUtcNow();
        var midnight = new DateTimeOffset(2026, 10, 1, 21, 0, 0, TimeSpan.Zero);

        // In Istanbul, 1 October ends at 21:00 UTC.
        _time.Advance(TimeSpan.FromDays(3));
        (await ReportAsync(
            api,
            Event("a", customer, "api_calls", 10, start),
            Event("b", customer, "api_calls", 5, midnight.AddSeconds(-1)),
            Event("c", customer, "api_calls", 7, midnight),
            Event("d", customer, "api_calls", 1.5m, midnight.AddDays(1).AddHours(2)),
            Event("u1", customer, "seats", timestamp: start, properties: new { user_id = "ada" }),
            Event("u2", customer, "seats", timestamp: midnight.AddHours(1), properties: new { user_id = "ada" }),
            Event("u3", customer, "seats", timestamp: midnight.AddHours(2), properties: new { user_id = "grace" }),
            Event("other", await CustomerAsync(api), "api_calls", 99, start))).Accepted.ShouldBe(8);
        _time.Advance(TimeSpan.FromDays(42));
        var range = $"from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(start.AddDays(3).ToString("O"))}";

        var days = await GetAsync<CustomerUsageResponse>(api, $"/api/v1/customers/{customer}/usage?meter=api_calls&{range}");
        var seats = await GetAsync<CustomerUsageResponse>(api, $"/api/v1/customers/{customer}/usage?meter=seats&{range}");
        var months = await GetAsync<CustomerUsageResponse>(api, $"/api/v1/customers/{customer}/usage?meter=api_calls&interval=month");

        (days.Meter.Key, days.Interval, days.From, days.To, days.Quantity).ShouldBe(("api_calls", UsageInterval.Day, start, start.AddDays(3), 23.5m));
        days.Buckets.Select(bucket => (bucket.Start, bucket.End, bucket.Quantity)).ShouldBe(
        [
            (start, midnight, 15m),
            (midnight, midnight.AddDays(1), 7m),
            (midnight.AddDays(1), midnight.AddDays(2), 1.5m),
            (midnight.AddDays(2), start.AddDays(3), 0m),
        ]);
        seats.Quantity.ShouldBe(2, "distinct over the whole range, not the sum of its days");
        seats.Buckets.Select(bucket => bucket.Quantity).ShouldBe([1m, 2m, 0m, 0m]);
        months.Buckets.Count.ShouldBe(12, "this month so far and the eleven before it");
        months.Buckets.Select(bucket => (bucket.Start, bucket.Quantity)).TakeLast(2).ShouldBe(
        [
            (new DateTimeOffset(2026, 9, 30, 21, 0, 0, TimeSpan.Zero), 23.5m),
            (new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero), 0m),
        ]);
        months.Buckets[^1].End.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    [Trait("Requirement", "USG-8")]
    public async Task UsageSummaries_RefuseWhatTheyCannotAnswer()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        using var writer = await host.CreateClientWithAsync(TahsilPermissions.UsageWrite);
        var customer = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-base-try");
        var now = Uri.EscapeDataString(_time.GetUtcNow().ToString("O"));

        using var noMeter = await api.GetPathAsync($"/api/v1/customers/{customer}/usage");
        using var unknownMeter = await api.GetPathAsync($"/api/v1/customers/{customer}/usage?meter=nothing");
        using var unknownCustomer = await api.GetPathAsync($"/api/v1/customers/{Guid.CreateVersion7()}/usage?meter=api_calls");
        using var backwards = await api.GetPathAsync($"/api/v1/customers/{customer}/usage?meter=api_calls&from={now}&to={now}");
        using var tooLong = await api.GetPathAsync($"/api/v1/customers/{customer}/usage?meter=api_calls&from=2020-01-01T00:00:00Z");
        using var oddInterval = await api.GetPathAsync($"/api/v1/customers/{customer}/usage?meter=api_calls&interval=week");
        using var forbidden = await writer.GetPathAsync($"/api/v1/customers/{customer}/usage?meter=api_calls");
        using var unknownSubscription = await api.GetPathAsync($"/api/v1/subscriptions/{Guid.CreateVersion7()}/usage");
        var unmetered = await GetAsync<SubscriptionUsageResponse>(api, $"/api/v1/subscriptions/{subscription.Id}/usage");

        (noMeter.StatusCode, await noMeter.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "request.invalid"));
        (unknownMeter.StatusCode, await unknownMeter.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "meter.not_found"));
        (unknownCustomer.StatusCode, await unknownCustomer.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "customer.not_found"));
        (backwards.StatusCode, await backwards.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "request.invalid"));
        (tooLong.StatusCode, await tooLong.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "request.invalid"));
        (oddInterval.StatusCode, await oddInterval.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "request.invalid"));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (unknownSubscription.StatusCode, await unknownSubscription.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "subscription.not_found"));
        unmetered.Items.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "USG-9")]
    [Trait("Requirement", "ADM-1")]
    public async Task ASubscriptionsUsageSoFar_IsShownWithWhatItWouldBill_ThroughTheApiTheAdminPanelAndThePortal()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-base-try", "api-calls-try", "requests-try");
        var second = subscription.CurrentPeriodEnd;
        _time.Advance(TimeSpan.FromDays(3));
        (await ReportAsync(api, Event("old", customer, "api_calls", 5_000))).Accepted.ShouldBe(1);

        // The first period's usage is on its invoice; the summary is about the period that runs.
        _time.Advance(second.AddHours(2) - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
        _time.Advance(TimeSpan.FromHours(2));
        await host.RunWorkersUntilIdleAsync();
        (await ReportAsync(api, Event("x", customer, "api_calls", 1_400), Event("r", customer, "requests"))).Accepted.ShouldBe(2);
        _time.Advance(TimeSpan.FromMinutes(1));

        var usage = await GetAsync<SubscriptionUsageResponse>(api, $"/api/v1/subscriptions/{subscription.Id}/usage");
        usage.Items.Select(item => (item.Meter.Key, item.Price.Key, item.PeriodStart, item.PeriodEnd, item.Quantity, item.Amount, item.Currency)).ShouldBe(
        [
            ("api_calls", "api-calls-try", second, second.AddMonths(1), 1_400m, 200L, "TRY"),
            ("requests", "requests-try", second, second.AddMonths(1), 1m, 10L, "TRY"),
        ], ignoreOrder: true);

        await using var admin = new AdminComponents(host);
        var detail = admin.Render<SubscriptionDetail>(parameters => parameters.Add(page => page.Id, subscription.Id));
        detail.WaitForAssertion(() => detail.FindAll("#usage tbody tr").Select(row => row.TextContent).ShouldContain(row => row.Contains("API calls") && row.Contains("1,400") && row.Contains("2.00 TRY")));
        var customerPage = admin.Render<CustomerUsage>(parameters => parameters.Add(page => page.CustomerId, customer));
        customerPage.WaitForAssertion(() => customerPage.Find("#usage-total").TextContent.ShouldBe("6,400 in the last 30 days."));
        customerPage.FindAll("#usage-days tbody tr").Select(row => row.QuerySelectorAll("td")[1].TextContent).ShouldBe(["1,400", "5,000"], "the newest day first");

        using var created = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "en" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        using var browser = Open(host);
        using var opened = await browser.GetAsync((await created.ReadAsync<PortalSessionResponse>()).Url, CancellationToken);
        var portal = await ParseAsync(opened);
        portal.QuerySelectorAll("#subscriptions .usage").Select(hint => hint.TextContent).ShouldBe(
            ["API calls: 1,400 so far this period (₺2.00)", "Requests: 1 so far this period (₺0.10)"], ignoreOrder: true);
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null) => IsolatedHosts.Create(server, server.NewDatabase("usage_summary"), _time, settings: settings);
}
