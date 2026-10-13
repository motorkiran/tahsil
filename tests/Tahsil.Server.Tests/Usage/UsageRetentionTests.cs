using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Usage;
using static Tahsil.Server.Tests.Usage.UsageScenarios;

namespace Tahsil.Server.Tests.Usage;

/// <summary>What becomes of usage events at volume and with time: retention (USG-13) and the ingestion target (USG-6).</summary>
public sealed class UsageRetentionTests(TahsilServerFixture server, ITestOutputHelper output)
{
    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "USG-13")]
    public async Task OldEvents_AreDeleted_ButNeverBeforeTheirPeriodIsInvoiced()
    {
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("usage_retention"), _time);
        using var api = await ApiAsync(host);
        var subscriber = await CustomerAsync(api);
        var canceled = await CustomerAsync(api);
        var visitor = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, subscriber, "api-calls-try");
        var ended = await SubscribeAsync(api, canceled, "api-calls-try");
        _time.Advance(TimeSpan.FromDays(1));
        (await ReportAsync(
            api,
            Event("unbilled", subscriber, "api_calls", 10),
            Event("abandoned", canceled, "api_calls", 10),
            Event("unbillable", visitor, "api_calls", 10))).Accepted.ShouldBe(3);
        using (var canceling = await api.PostJsonAsync($"/api/v1/subscriptions/{ended.Id}/cancel", new CancelSubscriptionRequest { At = "now", InvoiceNow = false }))
        {
            canceling.StatusCode.ShouldBe(HttpStatusCode.OK, await canceling.Content.ReadAsStringAsync(CancellationToken));
        }

        // The workers do not run: the subscriber's usage stays to be invoiced while the retention period passes.
        _time.Advance(TimeSpan.FromDays(401));
        (await ReportAsync(api, Event("recent", visitor, "api_calls", 10))).Accepted.ShouldBe(1);
        var retention = host.Services.GetRequiredService<UsageRetention>();

        (await retention.RunOnceAsync(CancellationToken)).ShouldBeTrue();
        var afterFirst = await IdentifiersAsync(host);
        await host.ExecuteSqlAsync($"UPDATE tahsil.subscription_items SET usage_billed_until = {_time.GetUtcNow().AddDays(-30)} WHERE subscription_id = {subscription.Id}");
        (await retention.RunOnceAsync(CancellationToken)).ShouldBeTrue();

        afterFirst.ShouldBe(["recent", "unbilled"], "what no subscription will invoice goes when it is old; what one still has to invoice stays");
        (await IdentifiersAsync(host)).ShouldBe(["recent"]);
    }

    /// <summary>
    /// Opt-in (<c>TAHSIL_LOAD_TESTS=1</c>): timings depend on the machine. Reports full batches for customers whose
    /// subscriptions bill the meter, several at once, and expects the target of 1,000 events per second.
    /// </summary>
    [Fact]
    [Trait("Requirement", "USG-6")]
    [Trait("Category", "Load")]
    public async Task Ingestion_TakesAThousandEventsPerSecond()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TAHSIL_LOAD_TESTS") == "1", "Load tests run when TAHSIL_LOAD_TESTS=1.");
        const int Customers = 100;
        const int Batches = 60;
        const int BatchSize = 1000;
        const int Parallel = 4;
        await using var host = IsolatedHosts.Create(
            server, server.NewDatabase("usage_load"), new FakeTimeProvider(DateTimeOffset.UtcNow), settings: new Dictionary<string, string?>
            {
                ["Tahsil:RateLimits:UsageBatchesPerSecond"] = "1000",
                ["Tahsil:RateLimits:ApiRequestsPerSecond"] = "1000",
                ["Tahsil:RateLimits:ApiBurst"] = "1000",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
            });
        using var api = await ApiAsync(host);
        var customers = new List<Guid>();
        for (var index = 0; index < Customers; index++)
        {
            customers.Add(await CustomerAsync(api));
            await SubscribeAsync(api, customers[index], "api-calls-try");
        }

        (await ReportAsync(api, Event("warm-up", customers[0], "api_calls"))).Accepted.ShouldBe(1);
        var bodies = Enumerable.Range(0, Batches)
            .Select(batch => new { events = Enumerable.Range(0, BatchSize).Select(index => Event($"load:{batch}:{index}", customers[(batch + index) % Customers], "api_calls", 1.5m, properties: new { region = "eu", batch })).ToArray() })
            .ToList();

        var watch = Stopwatch.StartNew();
        var accepted = 0;
        await System.Threading.Tasks.Parallel.ForEachAsync(bodies, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = CancellationToken }, async (body, _) =>
        {
            using var response = await api.PostJsonAsync("/api/v1/usage-events", body);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            Interlocked.Add(ref accepted, (await response.ReadAsync<ReportUsageResponse>()).Accepted);
        });
        watch.Stop();

        var perSecond = accepted / watch.Elapsed.TotalSeconds;
        output.WriteLine(FormattableString.Invariant($"{accepted} events in {watch.Elapsed.TotalSeconds:0.00} s: {perSecond:0} events per second, {Parallel} batches at once"));
        accepted.ShouldBe(Batches * BatchSize);
        perSecond.ShouldBeGreaterThan(1000);
    }

    private static async Task<List<string>> IdentifiersAsync(WebApplicationFactory<Program> host) =>
        await host.QueryAsync<string>($"SELECT identifier AS \"Value\" FROM tahsil.usage_events ORDER BY identifier");
}
