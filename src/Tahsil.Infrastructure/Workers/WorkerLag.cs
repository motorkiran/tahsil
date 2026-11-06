using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Workers;

/// <summary>
/// How far behind the workers are (ENG §11): per queue, now minus the due time of its oldest item, 0 when nothing is
/// overdue. Published as the <c>tahsil.worker.lag</c> gauge and read by the readiness probe.
/// </summary>
public sealed class WorkerLag
{
    /// <summary>The lag above which the readiness probe warns.</summary>
    public static readonly TimeSpan WarningThreshold = TimeSpan.FromMinutes(10);

    private volatile IReadOnlyDictionary<string, TimeSpan> _lags = new Dictionary<string, TimeSpan>();

    public WorkerLag() =>
        TahsilDiagnostics.Meter.CreateObservableGauge(
            "tahsil.worker.lag",
            () => _lags.Select(lag => new Measurement<double>(lag.Value.TotalSeconds, new KeyValuePair<string, object?>("queue", lag.Key))),
            unit: "s",
            description: "How long the oldest due item of each worker queue has waited.");

    /// <summary>The lags of the last measurement; empty before the first.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Lags => _lags;

    internal void Record(IReadOnlyList<QueueDue> queues, DateTimeOffset now) =>
        _lags = queues.ToDictionary(queue => queue.Queue, queue => queue.Due is { } due && due < now ? now - due : TimeSpan.Zero, StringComparer.Ordinal);
}

/// <summary>Measures <see cref="WorkerLag"/> every minute on instances that run the workers.</summary>
internal sealed partial class WorkerLagMonitor(
    IServiceScopeFactory scopes, WorkerLag lag, BillingClock clock, TimeProvider time, IOptions<WorkerOptions> options, ILogger<WorkerLagMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public async Task MeasureAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        lag.Record(await DueQueues.OldestAsync(scope.ServiceProvider.GetRequiredService<TahsilDbContext>(), cancellationToken), clock.GetUtcNow());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await MeasureAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database may be unavailable; the readiness probe reports that itself.
                LogMeasurementFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(LogLevel.Warning, "Could not measure the workers' lag")]
    private static partial void LogMeasurementFailed(ILogger logger, Exception exception);
}
