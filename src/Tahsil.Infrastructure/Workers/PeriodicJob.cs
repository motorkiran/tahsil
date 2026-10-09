using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Workers;

/// <summary>
/// A job that runs on an interval, on one instance at a time: the instance that takes the job's advisory lock runs
/// it, and the others skip the round. The timer runs on the billing clock, so tests control it.
/// </summary>
internal abstract partial class PeriodicJob(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> options,
    ILogger logger) : BackgroundService
{
    public abstract string Name { get; }

    /// <summary>The job's key in <see cref="AdvisoryLockKeys"/>.</summary>
    protected abstract long LockKey { get; }

    protected abstract TimeSpan Interval { get; }

    protected BillingClock Clock => clock;

    /// <summary>Runs the job once if no other instance is running it; returns whether it ran.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        // A session lock on a connection held for the whole run, so the job itself may use short transactions.
        await using var lockScope = scopes.CreateAsyncScope();
        var database = lockScope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database;
        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            var locked = await database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(cancellationToken);
            if (!locked)
            {
                return false;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunAsync(scope.ServiceProvider, cancellationToken);
                return true;
            }
            finally
            {
                await database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

    protected abstract Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database may be unavailable; try again at the next tick.
                LogRunFailed(logger, exception, Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(LogLevel.Warning, "Periodic job {Job} failed; it runs again at the next interval")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string job);
}
