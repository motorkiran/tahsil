using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Domain.Auditing;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Workers;

/// <summary>
/// The base of every worker: it claims due items one at a time with <c>FOR UPDATE SKIP LOCKED</c>, so any number
/// of instances can share the work without processing an item twice at once, and processes each in its own unit of
/// work.
/// <list type="bullet">
/// <item>Workers that only touch the database process an item inside the transaction that locked it.</item>
/// <item>
/// Workers that call other services (<see cref="CallsOtherServices"/>) never hold a transaction during the call:
/// they move the item's due time forward by a lease, commit, and then process it. If the instance dies, the item is
/// due again when the lease runs out.
/// </item>
/// </list>
/// A failing item is postponed by an increasing backoff and reported as needing attention after ten failures in a
/// row, without holding up the other items. Lost optimistic concurrency races are retried at once, three times.
/// </summary>
internal abstract partial class DueWorker(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> options,
    WorkerSignals signals,
    ILogger logger) : BackgroundService, IRunnableWorker
{
    private const string ItemSavepoint = "item";
    private const int ConflictAttempts = 3;
    private const int LastErrorMaxLength = 2000;

    /// <summary>The stable name of the worker, used for signals, logs and failure records.</summary>
    public abstract string Name { get; }

    /// <summary>Whether processing calls providers or other services, so items are leased instead of locked.</summary>
    protected abstract bool CallsOtherServices { get; }

    protected BillingClock Clock => clock;

    protected IServiceScopeFactory Scopes => scopes;

    protected WorkerOptions Options => options.Value;

    protected virtual TimeSpan PollingInterval => Options.PollingInterval;

    protected virtual int BatchSize => Options.BatchSize;

    /// <summary>How many items this instance processes at once. Only workers that lease their items take more than one.</summary>
    protected virtual int Concurrency => 1;

    /// <summary>
    /// Processes due items until none is left or a batch is done; returns how many it took. The hosted loop calls it,
    /// and tests call it directly.
    /// </summary>
    public async Task<int> RunBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = 0;
        var processed = 0;

        async Task DrainAsync()
        {
            var conflicts = new Dictionary<Guid, int>();
            while (Interlocked.Increment(ref claimed) <= BatchSize && await ProcessNextAsync(conflicts, cancellationToken))
            {
                Interlocked.Increment(ref processed);
            }
        }

        var lanes = CallsOtherServices ? Math.Max(Concurrency, 1) : 1;
        await Task.WhenAll(Enumerable.Range(0, lanes).Select(_ => DrainAsync()));
        return processed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await RunHostedBatchAsync(stoppingToken) == BatchSize)
                {
                    // A full batch means more items may be due.
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database may be unavailable; try again at the next poll.
                LogBatchFailed(logger, exception, Name);
            }

            await signals.WaitAsync(Name, PollingInterval, stoppingToken);
        }
    }

    /// <summary>
    /// A batch of the hosted loop. In Test mode it runs under the shared <see cref="AdvisoryLockKeys.TimeTravelWorkers"/>
    /// lock and is skipped while a time travel runs: the travel processes everything itself, and moving the clock
    /// past a lease this loop holds would hand the leased item to another worker while this one still processes it.
    /// </summary>
    private async Task<int> RunHostedBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<IOptions<ServerOptions>>().Value.IsTestMode)
        {
            return await RunBatchAsync(cancellationToken);
        }

        var database = scope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database;
        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await database.SqlQuery<bool>($"SELECT pg_try_advisory_lock_shared({AdvisoryLockKeys.TimeTravelWorkers}) AS \"Value\"").SingleAsync(cancellationToken))
            {
                return 0;
            }

            try
            {
                return await RunBatchAsync(cancellationToken);
            }
            finally
            {
                await database.ExecuteSqlAsync($"SELECT pg_advisory_unlock_shared({AdvisoryLockKeys.TimeTravelWorkers})", CancellationToken.None);
            }
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Locks the next item due at <paramref name="now"/> with <c>FOR UPDATE SKIP LOCKED</c>, in the caller's
    /// transaction, and returns its ID; <see langword="null"/> when nothing is due.
    /// </summary>
    protected abstract Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Moves the item's due time to <paramref name="until"/> (a lease or a backoff).</summary>
    protected abstract Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>
    /// Processes one item and moves it on (sets its next due time, or clears it). Workers that only touch the
    /// database run inside the claim transaction, with the scope's context; the others manage their own transactions.
    /// </summary>
    protected abstract Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    private async Task<bool> ProcessNextAsync(Dictionary<Guid, int> conflicts, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
        var now = clock.GetUtcNow();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await LockNextDueAsync(context, now, cancellationToken) is not { } id)
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        if (CallsOtherServices)
        {
            await PostponeAsync(context, id, now + Options.Lease, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await ProcessLeasedAsync(id, now, cancellationToken);
            Measure(started);
            return true;
        }

        await transaction.CreateSavepointAsync(ItemSavepoint, cancellationToken);
        try
        {
            await ProcessAsync(scope.ServiceProvider, id, now, cancellationToken);
            await ClearFailuresAsync(context, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Measure(started);
        }
        catch (DbUpdateConcurrencyException) when (CanRetry(conflicts, id))
        {
            // Rolled back; the item is still the most overdue one and is processed again right away.
        }
        catch (Exception exception) when (!IsShutdown(exception, cancellationToken))
        {
            await transaction.RollbackToSavepointAsync(ItemSavepoint, cancellationToken);
            context.ChangeTracker.Clear();
            await RecordFailureAsync(scope.ServiceProvider, context, id, now, exception, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>Counts an item this worker took, and how long it took (ENG §11).</summary>
    private void Measure(long started)
    {
        var worker = new KeyValuePair<string, object?>("worker", Name);
        TahsilDiagnostics.ItemsProcessed.Add(1, worker);
        TahsilDiagnostics.ItemDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, worker);
    }

    private async Task ProcessLeasedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await ProcessAsync(scope.ServiceProvider, id, now, cancellationToken);
                await ClearFailuresAsync(scope.ServiceProvider.GetRequiredService<TahsilDbContext>(), id, cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < ConflictAttempts)
            {
                // Lost a race with a concurrent change; the item is still leased to this instance.
            }
            catch (Exception exception) when (!IsShutdown(exception, cancellationToken))
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                await RecordFailureAsync(scope.ServiceProvider, context, id, clock.GetUtcNow(), exception, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }
    }

    private async Task RecordFailureAsync(
        IServiceProvider services, TahsilDbContext context, Guid id, DateTimeOffset now, Exception exception, CancellationToken cancellationToken)
    {
        LogItemFailed(logger, exception, Name, id);
        TahsilDiagnostics.ItemFailures.Add(1, new KeyValuePair<string, object?>("worker", Name));
        var message = Redaction.Mask(exception.Message);
        var error = message.Length > LastErrorMaxLength ? message[..LastErrorMaxLength] : message;

        var failure = (await context.Database.SqlQuery<FailureState>($"""
            INSERT INTO tahsil.worker_failures (worker, item_id, failure_count, first_failed_at, last_failed_at, last_error)
            VALUES ({Name}, {id}, 1, {now}, {now}, {error})
            ON CONFLICT (worker, item_id) DO UPDATE SET
                failure_count = worker_failures.failure_count + 1,
                last_failed_at = excluded.last_failed_at,
                last_error = excluded.last_error
            RETURNING failure_count AS count, attention_raised_at
            """).ToListAsync(cancellationToken)).Single();

        await PostponeAsync(context, id, now + WorkerFailure.BackoffAfter(failure.Count), cancellationToken);

        if (failure.Count >= WorkerFailure.AttentionThreshold && failure.AttentionRaisedAt is null)
        {
            await context.Database.ExecuteSqlAsync(
                $"UPDATE tahsil.worker_failures SET attention_raised_at = {now} WHERE worker = {Name} AND item_id = {id}",
                cancellationToken);
            services.GetRequiredService<IAuditLog>().Record(
                AuditActions.WorkerAttentionRaised,
                AuditSubject.Of(Name, id),
                new Dictionary<string, object?> { ["failures"] = failure.Count, ["error"] = error });
            await context.SaveChangesAsync(cancellationToken);
            TahsilDiagnostics.AttentionRaised.Add(1, new KeyValuePair<string, object?>("worker", Name));
            LogAttentionRaised(logger, Name, id, failure.Count);
        }
    }

    private Task<int> ClearFailuresAsync(TahsilDbContext context, Guid id, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"DELETE FROM tahsil.worker_failures WHERE worker = {Name} AND item_id = {id}", cancellationToken);

    private static bool CanRetry(Dictionary<Guid, int> conflicts, Guid id)
    {
        conflicts[id] = conflicts.GetValueOrDefault(id) + 1;
        return conflicts[id] < ConflictAttempts;
    }

    private static bool IsShutdown(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    [LoggerMessage(LogLevel.Error, "Worker {Worker} could not process a batch")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception, string worker);

    [LoggerMessage(LogLevel.Warning, "Worker {Worker} failed to process item {ItemId}; it is retried later")]
    private static partial void LogItemFailed(ILogger logger, Exception exception, string worker, Guid itemId);

    [LoggerMessage(LogLevel.Error, "Worker {Worker} failed to process item {ItemId} {Failures} times in a row; it needs attention")]
    private static partial void LogAttentionRaised(ILogger logger, string worker, Guid itemId, int failures);

    private sealed record FailureState(int Count, DateTimeOffset? AttentionRaisedAt);
}
