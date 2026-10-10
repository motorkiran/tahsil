using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Webhooks;
using Tahsil.Domain.Webhooks;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Webhooks;

/// <summary>
/// Sends due webhook deliveries and retries failed ones with growing delays (EVT-9 to EVT-11). A batch is claimed in a
/// short transaction that leases its deliveries (<c>FOR UPDATE SKIP LOCKED</c>), at most a few per endpoint, and sent
/// outside of it, so any number of instances can share the work and a slow endpoint is not flooded. An instance that
/// stops hands back what it has not sent; one that crashes lets the lease run out: delivery is at least once.
/// Copied from Kimlik, with per-endpoint concurrency, two signatures during rotations, and the billing clock for due times.
/// </summary>
internal sealed partial class WebhookDeliveryService(
    IServiceScopeFactory scopes,
    WorkerSignals signals,
    IOptions<WebhookOptions> options,
    IOptions<WorkerOptions> workerOptions,
    BillingClock clock,
    ILogger<WebhookDeliveryService> logger) : BackgroundService, IRunnableWorker
{
    public string Name => WebhookFanOut.WorkerName;

    /// <summary>Sends one batch; returns how many deliveries it attempted. The hosted loop calls it, and tests call it directly.</summary>
    public async Task<int> RunBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
        var deliveries = await ClaimAsync(context, cancellationToken);
        if (deliveries.Count == 0)
        {
            return 0;
        }

        // The batch is leased now: only the sends stop when the instance does, so that the rest is handed back below.
        var endpointIds = deliveries.Select(delivery => delivery.EndpointId).Distinct().ToList();
        var endpoints = await context.WebhookEndpoints.Where(endpoint => endpointIds.Contains(endpoint.Id)).ToDictionaryAsync(endpoint => endpoint.Id, CancellationToken.None);
        var sender = scope.ServiceProvider.GetRequiredService<WebhookSender>();
        var encryption = scope.ServiceProvider.GetRequiredService<ISecretEncryption>();
        var now = clock.GetUtcNow();

        var sends = deliveries
            .Select(delivery => (Delivery: delivery, Attempt: AttemptAsync(delivery, endpoints.GetValueOrDefault(delivery.EndpointId), sender, encryption, now, cancellationToken)))
            .ToList();
        try
        {
            await Task.WhenAll(sends.Select(send => send.Attempt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The instance is stopping; what was not sent is handed back below.
        }

        now = clock.GetUtcNow();
        foreach (var (delivery, attempt) in sends)
        {
            if (attempt.IsCompletedSuccessfully)
            {
                Record(delivery, endpoints.GetValueOrDefault(delivery.EndpointId), attempt.Result, now);
            }
            else
            {
                // Another instance makes the attempt now, instead of when the lease runs out.
                delivery.AbandonAttempt(now);
            }
        }

        // Saved even while stopping, so that neither the outcomes nor the handed-back deliveries are lost.
        await context.SaveChangesAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return deliveries.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await RunBatchAsync(stoppingToken) == options.Value.BatchSize)
                {
                    // A full batch means more deliveries may be due.
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database may be unavailable; try again at the next poll.
                LogSendingFailed(logger, exception);
            }

            await signals.WaitAsync(Name, options.Value.PollingInterval, stoppingToken);
        }
    }

    /// <summary>Leases due deliveries for long enough to send them, at most the endpoint concurrency for each endpoint (EVT-10).</summary>
    private async Task<List<WebhookDelivery>> ClaimAsync(TahsilDbContext context, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var settings = options.Value;
        var leaseUntil = now + settings.Timeout + TimeSpan.FromMinutes(1);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var deliveries = await context.WebhookDeliveries
            .FromSql($"""
                SELECT * FROM tahsil.webhook_deliveries
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id, row_number() OVER (PARTITION BY endpoint_id ORDER BY next_attempt_at) AS position
                        FROM tahsil.webhook_deliveries
                        WHERE status = 'pending' AND next_attempt_at <= {now}
                    ) AS due
                    WHERE position <= {settings.EndpointConcurrency})
                ORDER BY next_attempt_at
                LIMIT {settings.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var delivery in deliveries)
        {
            delivery.BeginAttempt(now, leaseUntil);
        }

        // Committed whatever happens, so that a lease is either taken, and handed back if the instance stops, or not taken.
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return deliveries;
    }

    private static async Task<WebhookAttempt?> AttemptAsync(
        WebhookDelivery delivery, WebhookEndpoint? endpoint, WebhookSender sender, ISecretEncryption encryption, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Test events go even to disabled endpoints: trying one out comes before turning it on.
        if (endpoint is null || (!endpoint.Enabled && delivery.EventType != WebhookEvents.TestEventType))
        {
            return null;
        }

        List<string> secrets;
        try
        {
            secrets = [.. endpoint.SigningSecrets(now).Select(secret => encryption.DecryptString(secret, SecretPurposes.WebhookEndpointSecret, endpoint.Id))];
        }
        catch (CryptographicException)
        {
            return new WebhookAttempt(false, null, null, "The endpoint's secret cannot be decrypted with the current master key; rotate it.");
        }

        return await sender.SendAsync(endpoint.Url, secrets, delivery, cancellationToken);
    }

    /// <summary>Records the outcome; a failed attempt is retried after the next delay, until none is left.</summary>
    private void Record(WebhookDelivery delivery, WebhookEndpoint? endpoint, WebhookAttempt? attempt, DateTimeOffset now)
    {
        if (attempt is null || endpoint is null)
        {
            delivery.Fail(null, null, "The endpoint is disabled.", retryAt: null);
            return;
        }

        if (attempt.Succeeded)
        {
            delivery.Succeed(attempt.StatusCode!.Value, attempt.ResponseBody);
            endpoint.RecordSuccess();
            return;
        }

        var delays = options.Value.Delays;
        DateTimeOffset? retryAt = delivery.Attempts <= delays.Count ? now + delays[delivery.Attempts - 1] : null;
        delivery.Fail(attempt.StatusCode, attempt.ResponseBody, attempt.Error, retryAt);
        endpoint.RecordFailure(now);
        if (retryAt is null)
        {
            LogDeliveryFailed(logger, delivery.Id, delivery.EventType, endpoint.Url, delivery.Attempts);
        }
    }

    [LoggerMessage(LogLevel.Error, "Sending webhooks failed")]
    private static partial void LogSendingFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Webhook delivery {DeliveryId} ({EventType}) to {Url} failed after {Attempts} attempts and will not be retried")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid deliveryId, string eventType, string url, int attempts);
}

/// <summary>Deletes the delivery log older than the retention period (EVT-14), every hour.</summary>
internal sealed class WebhookDeliveryCleanup(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> workerOptions, IOptions<WebhookOptions> options, ILogger<WebhookDeliveryCleanup> logger)
    : PeriodicJob(scopes, clock, workerOptions, logger)
{
    public override string Name => "webhook-delivery-cleanup";

    protected override long LockKey => AdvisoryLockKeys.WebhookDeliveryCleanup;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var cutoff = Clock.GetUtcNow() - options.Value.RetentionPeriod;
        await services.GetRequiredService<TahsilDbContext>().WebhookDeliveries
            .Where(delivery => delivery.Status != WebhookDeliveryStatus.Pending && delivery.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
