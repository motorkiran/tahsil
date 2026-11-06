using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Performance;

/// <summary>
/// The performance targets, measured in process against a test database (opt-in with <c>TAHSIL_LOAD_TESTS=1</c>:
/// timings depend on the machine). Request times are measured by the caller without a network between them, so they
/// are the server's time. Each test also appends its figures to <c>load-results.txt</c> next to the test binaries.
/// </summary>
[Trait("Category", "Load")]
public sealed class LoadTests(TahsilServerFixture server, ITestOutputHelper output)
{
    private const int Parallel = 8;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApiReadsAndWrites_StayWithinTheirTargets()
    {
        SkipUnlessAsked();
        await using var host = Host("load_api");
        using var api = await ClientAsync(host);
        var customers = new ConcurrentBag<Guid>();

        var writes = await MeasureAsync(1_000, async index =>
        {
            using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = $"Customer {index}", Email = $"customer{index}@example.com" });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            customers.Add((await created.ReadAsync<CustomerResponse>()).Id);
        });
        var ids = customers.ToArray();
        var reads = await MeasureAsync(2_000, async index =>
        {
            using var read = index % 2 == 0
                ? await api.GetPathAsync($"/api/v1/customers/{ids[index % ids.Length]}")
                : await api.GetPathAsync("/api/v1/customers?limit=20");
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        });

        Report("API writes (create a customer)", writes);
        Report("API reads (a customer, a page of 20)", reads);
        writes.P95.ShouldBeLessThan(150);
        reads.P95.ShouldBeLessThan(100);
    }

    [Fact]
    public async Task HostedPages_RenderWithinTheTarget()
    {
        SkipUnlessAsked();
        await using var host = Host("load_pages");
        using var api = await ClientAsync(host);
        var customer = await CompanyAsync(api);
        await SaveCardAsync(api, customer, "4242424242424242");
        var subscription = await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        var invoice = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{subscription.LatestInvoice!.Id}");
        using var portalSession = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing" });
        var portal = (await portalSession.ReadAsync<PortalSessionResponse>()).Url;
        using var checkoutSession = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Payment,
            Items = [new CheckoutItemRequest { Price = "setup-fee-try", Quantity = 1 }],
            SuccessUrl = "https://shop.example.com/done",
            CancelUrl = "https://shop.example.com/cart",
        });
        var checkout = (await checkoutSession.ReadAsync<CheckoutSessionResponse>()).Url!;
        using var browser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var (name, url) in new[] { ("checkout page", checkout), ("hosted invoice page", invoice.HostedInvoiceUrl!), ("portal overview", portal) })
        {
            var timing = await MeasureAsync(500, async _ =>
            {
                using var page = await browser.GetAsync(url, CancellationToken);
                page.StatusCode.ShouldBe(HttpStatusCode.OK);
            });
            Report($"Hosted pages ({name})", timing);
            timing.P95.ShouldBeLessThan(150);
        }
    }

    [Fact]
    public async Task Renewals_ReachTwentyThousandAnHour()
    {
        SkipUnlessAsked();
        var count = int.TryParse(Environment.GetEnvironmentVariable("TAHSIL_LOAD_RENEWALS"), CultureInfo.InvariantCulture, out var asked) ? asked : 1_000;
        await using var host = Host("load_renewals");
        using var api = await ClientAsync(host);
        var subscriptions = new ConcurrentBag<Guid>();
        await System.Threading.Tasks.Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = CancellationToken }, async (_, _) =>
        {
            var customer = await CompanyAsync(api);
            await SaveCardAsync(api, customer, "4242424242424242");
            subscriptions.Add((await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] })).Id);
        });
        await host.RunWorkersUntilIdleAsync();

        // Every subscription renews at once: the cycle invoices, then their finalization and charge an hour later.
        var watch = Stopwatch.StartNew();
        _time.Advance(TimeSpan.FromDays(30));
        await host.RunWorkersUntilIdleAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await host.RunWorkersUntilIdleAsync();
        watch.Stop();

        var paid = await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.invoices WHERE billing_reason = 'subscription_cycle' AND status = 'paid'");
        var perHour = count / watch.Elapsed.TotalHours;
        Write(FormattableString.Invariant($"Renewals: {count} invoiced, finalized and charged in {watch.Elapsed.TotalSeconds:0.0} s, {perHour:0} per hour"));
        paid.ShouldBe([count]);
        perHour.ShouldBeGreaterThan(20_000);
    }

    private static void SkipUnlessAsked() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TAHSIL_LOAD_TESTS") == "1", "Load tests run when TAHSIL_LOAD_TESTS=1.");

    /// <summary>Runs the requests, <see cref="Parallel"/> at once, after a warm-up of 50.</summary>
    private static async Task<Timing> MeasureAsync(int requests, Func<int, Task> request)
    {
        for (var index = 0; index < 50; index++)
        {
            await request(requests + index);
        }

        var durations = new ConcurrentBag<double>();
        var watch = Stopwatch.StartNew();
        await System.Threading.Tasks.Parallel.ForEachAsync(Enumerable.Range(0, requests), new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = CancellationToken }, async (index, _) =>
        {
            var started = Stopwatch.GetTimestamp();
            await request(index);
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        });
        watch.Stop();

        var sorted = durations.Order().ToList();
        return new Timing(sorted[sorted.Count / 2], sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1], sorted.Count / watch.Elapsed.TotalSeconds);
    }

    private void Report(string name, Timing timing) =>
        Write(FormattableString.Invariant($"{name}: median {timing.Median:0.0} ms, p95 {timing.P95:0.0} ms, {timing.PerSecond:0} per second, {Parallel} at once"));

    private void Write(string line)
    {
        output.WriteLine(line);
        File.AppendAllLines(Path.Combine(AppContext.BaseDirectory, "load-results.txt"), [line]);
    }

    private WebApplicationFactory<Program> Host(string database) =>
        IsolatedHosts.Create(server, server.NewDatabase(database), _time, settings: new Dictionary<string, string?>
        {
            ["Tahsil:RateLimits:ApiRequestsPerSecond"] = "100000",
            ["Tahsil:RateLimits:ApiBurst"] = "100000",
            ["Tahsil:RateLimits:HostedPageRequestsPerMinute"] = "1000000",
            ["Logging:LogLevel:Default"] = "Warning",
        });

    private sealed record Timing(double Median, double P95, double PerSecond);
}
