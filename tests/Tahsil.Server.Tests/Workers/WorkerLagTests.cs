using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Server.Tests.Workers;

/// <summary>How far behind the workers are (ENG §11): a gauge per queue, and a readiness warning past ten minutes.</summary>
[Trait("Requirement", "ENG-6")]
public sealed class WorkerLagTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQueueBehindByMoreThanTenMinutes_MakesReadinessWarn_WithoutFailingIt()
    {
        // The workers do not run here, so what is due stays due.
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("worker_lag"), _time);
        var monitor = host.Services.GetServices<IHostedService>().OfType<WorkerLagMonitor>().Single();
        var lag = host.Services.GetRequiredService<WorkerLag>();
        using var http = host.CreateClient();
        var now = _time.GetUtcNow();

        await host.ExecuteSqlAsync($"INSERT INTO tahsil.outbox_messages (id, type, payload, occurred_at, status, attempts, next_attempt_at) VALUES ({Guid.CreateVersion7()}, 'test.nothing', '{{}}'::jsonb, {now}, 'pending', 0, {now.AddMinutes(-5)})");
        await monitor.MeasureAsync(CancellationToken);
        using var recent = await http.GetAsync(new Uri("/health/ready", UriKind.Relative), CancellationToken);
        var recentLag = lag.Lags["outbox"];

        await host.ExecuteSqlAsync($"INSERT INTO tahsil.outbox_messages (id, type, payload, occurred_at, status, attempts, next_attempt_at) VALUES ({Guid.CreateVersion7()}, 'test.nothing', '{{}}'::jsonb, {now}, 'pending', 0, {now.AddMinutes(-20)})");
        await monitor.MeasureAsync(CancellationToken);
        using var behind = await http.GetAsync(new Uri("/health/ready", UriKind.Relative), CancellationToken);

        recentLag.ShouldBe(TimeSpan.FromMinutes(5));
        (recent.StatusCode, await recent.Content.ReadAsStringAsync(CancellationToken)).ShouldBe((HttpStatusCode.OK, "Healthy"));
        lag.Lags["outbox"].ShouldBe(TimeSpan.FromMinutes(20));
        lag.Lags["subscription-cycles"].ShouldBe(TimeSpan.Zero, "an empty queue is not behind");
        (behind.StatusCode, await behind.Content.ReadAsStringAsync(CancellationToken)).ShouldBe((HttpStatusCode.OK, "Degraded"), "a warning, not a reason to take the instance out of service");
    }
}
