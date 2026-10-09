using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Persistence.Conventions;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Outbox;

/// <summary>
/// Delivers pending outbox messages. A message is leased in a short transaction and its handler runs outside of
/// any transaction, so a failed commit can never repeat a side effect that already happened; the outcome is then
/// recorded on its own, with retries. Delivery is at least once: if the instance dies mid-way, the lease runs out
/// and the message is delivered again.
/// </summary>
internal sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    OutboxMessageTypes types,
    BillingClock clock,
    IOptions<WorkerOptions> workerOptions,
    IOptions<OutboxOptions> outboxOptions,
    WorkerSignals signals,
    ILogger<OutboxDispatcher> logger) : DueWorker(scopes, clock, workerOptions, signals, logger)
{
    public const string WorkerName = "outbox";

    private const int LastErrorMaxLength = 2000;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan[] RecordingRetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    private static readonly string Pending = DbEnum.Value(OutboxMessageStatus.Pending);
    private static readonly string Processed = DbEnum.Value(OutboxMessageStatus.Processed);
    private static readonly string Failed = DbEnum.Value(OutboxMessageStatus.Failed);

    public override string Name => WorkerName;

    protected override bool CallsOtherServices => true;

    protected override TimeSpan PollingInterval => outboxOptions.Value.PollingInterval;

    protected override int BatchSize => outboxOptions.Value.BatchSize;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.outbox_messages
            WHERE status = {Pending} AND next_attempt_at <= {now}
            ORDER BY next_attempt_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.outbox_messages SET next_attempt_at = {until} WHERE id = {id}", cancellationToken);

    protected override async Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = await services.GetRequiredService<TahsilDbContext>().OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.Status == OutboxMessageStatus.Pending, cancellationToken);
        if (message is null)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            // Each message gets its own scope, so a handler's unit of work never mixes with the dispatcher's.
            await using var handlerScope = Scopes.CreateAsyncScope();
            await types.DispatchAsync(message.Type, message.Payload, handlerScope.ServiceProvider, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = exception;
        }

        await RecordOutcomeAsync(message, failure, cancellationToken);
    }

    /// <summary>
    /// Records what happened. The side effect has already taken place, so a failure to record it is retried here
    /// instead of letting the message be delivered again.
    /// </summary>
    private async Task RecordOutcomeAsync(OutboxMessage message, Exception? failure, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var scope = Scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database;
                var now = Clock.GetUtcNow();

                if (failure is null)
                {
                    await database.ExecuteSqlAsync(
                        $"UPDATE tahsil.outbox_messages SET status = {Processed}, processed_at = {now}, last_error = NULL WHERE id = {message.Id}",
                        cancellationToken);
                    return;
                }

                var attempts = message.Attempts + 1;
                var error = failure.Message.Length > LastErrorMaxLength ? failure.Message[..LastErrorMaxLength] : failure.Message;
                if (attempts >= outboxOptions.Value.MaxAttempts)
                {
                    await database.ExecuteSqlAsync(
                        $"UPDATE tahsil.outbox_messages SET status = {Failed}, attempts = {attempts}, last_error = {error} WHERE id = {message.Id}",
                        cancellationToken);
                    LogMessageFailed(logger, failure, message.Id, message.Type, attempts);
                }
                else
                {
                    var delay = TimeSpan.FromTicks(Math.Min(FirstRetryDelay.Ticks << (attempts - 1), MaxRetryDelay.Ticks));
                    await database.ExecuteSqlAsync(
                        $"UPDATE tahsil.outbox_messages SET attempts = {attempts}, last_error = {error}, next_attempt_at = {now + delay} WHERE id = {message.Id}",
                        cancellationToken);
                    LogMessageRetrying(logger, failure, message.Id, message.Type, delay);
                }

                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt < RecordingRetryDelays.Length)
            {
                LogRecordingRetrying(logger, exception, message.Id);
                await Task.Delay(RecordingRetryDelays[attempt], cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Out of retries: the lease runs out and the message is delivered again, as after a crash.
                LogRecordingFailed(logger, exception, message.Id);
                return;
            }
        }
    }

    [LoggerMessage(LogLevel.Warning, "Outbox message {MessageId} ({Type}) failed; retrying in {Delay}")]
    private static partial void LogMessageRetrying(ILogger logger, Exception exception, Guid messageId, string type, TimeSpan delay);

    [LoggerMessage(LogLevel.Error, "Outbox message {MessageId} ({Type}) failed after {Attempts} attempts and will not be retried")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId, string type, int attempts);

    [LoggerMessage(LogLevel.Warning, "Recording the outcome of outbox message {MessageId} failed; retrying")]
    private static partial void LogRecordingRetrying(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(LogLevel.Error, "Recording the outcome of outbox message {MessageId} failed; it will be delivered again when its lease runs out")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, Guid messageId);
}
