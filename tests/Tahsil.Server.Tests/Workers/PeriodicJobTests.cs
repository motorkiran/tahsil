using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Server.Tests.Workers;

[Trait("Requirement", "FND-38")]
public sealed class PeriodicJobTests(TahsilServerFixture server)
{
    [Fact]
    public async Task AJob_RunsOnOneInstanceAtATime()
    {
        var time = new FakeTimeProvider(FakeBillingClock.Start);
        var database = server.NewDatabase("jobs");
        await using var first = IsolatedHosts.Create(server, database, time);
        await using var second = IsolatedHosts.Create(server, database, time);
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var blocking = ActivatorUtilities.CreateInstance<GatedJob>(first.Services, new Gate(started, gate.Task));
        var other = ActivatorUtilities.CreateInstance<GatedJob>(second.Services, new Gate(new TaskCompletionSource(), Task.CompletedTask));

        var running = blocking.RunOnceAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        (await other.RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        gate.SetResult();
        (await running).ShouldBeTrue();
        (await other.RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    internal sealed record Gate(TaskCompletionSource Started, Task Open);

    private sealed class GatedJob(
        IServiceScopeFactory scopes,
        BillingClock clock,
        IOptions<WorkerOptions> options,
        ILogger<GatedJob> logger,
        Gate gate) : PeriodicJob(scopes, clock, options, logger)
    {
        public override string Name => "gated";

        protected override long LockKey => 0x544148_53494C_7E57;

        protected override TimeSpan Interval => TimeSpan.FromHours(1);

        protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            gate.Started.TrySetResult();
            await gate.Open.WaitAsync(cancellationToken);
        }
    }
}
