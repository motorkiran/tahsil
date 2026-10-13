using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;
using ProviderRefundStatus = Tahsil.Providers.Abstractions.RefundStatus;
using ProviderStatus = Tahsil.Providers.Abstractions.PaymentStatus;

namespace Tahsil.Application.Payments;

/// <summary>How to answer the provider: status, content type and body, as its adapter says.</summary>
public sealed record NotificationAnswer(int Status, string ContentType, string Body);

/// <summary>
/// Takes provider notifications (PAY-23): the adapter verifies them, and only verified ones are stored, once per
/// provider event, with their body encrypted. Nothing else happens before the answer; the worker applies them later.
/// </summary>
public sealed partial class ProviderNotifications(
    ITahsilDbContext context, ProviderAccess providers, ISecretEncryption encryption, IWorkerSignals signals, BillingClock clock, ILogger<ProviderNotifications> logger)
{
    private static readonly Counter<long> Received = TahsilDiagnostics.Meter.CreateCounter<long>(
        "tahsil.provider_notifications", unit: "{notification}", description: "Provider notifications received, by connection and whether they were verified.");

    private static readonly NotificationAnswer NotFound = new(404, "text/plain", "Not found");

    public async Task<NotificationAnswer> ReceiveAsync(Guid connectionId, IncomingNotification notification, CancellationToken cancellationToken)
    {
        var connection = await context.ProviderConnections.AsNoTracking().SingleOrDefaultAsync(row => row.Id == connectionId, cancellationToken);
        if (connection is null || providers.For(connection) is not { } call)
        {
            return NotFound;
        }

        var result = await call.Provider.HandleNotificationAsync(call.Context, notification, cancellationToken);
        Received.Add(1, new KeyValuePair<string, object?>("connection", connectionId), new KeyValuePair<string, object?>("verified", result.Verified));
        var answer = new NotificationAnswer(result.Response.Status, result.Response.ContentType, result.Response.Body);
        if (!result.Verified)
        {
            LogUnverified(logger, connection.Key, notification.RemoteAddress?.ToString());
            return answer;
        }

        if (result.Deferred)
        {
            // The provider sends it again later; nothing is kept now.
            return answer;
        }

        if (result.ProviderEventId is { } eventId
            && await context.ProviderNotifications.AnyAsync(row => row.ConnectionId == connectionId && row.ProviderEventId == eventId, cancellationToken))
        {
            // A repeat of an event already stored: the provider gets the same answer and nothing is applied twice.
            return answer;
        }

        var stored = ProviderNotification.Receive(
            connectionId,
            result.ProviderEventId,
            result.EventType,
            NotificationPayload.Write(result.Updates),
            id => encryption.Encrypt(notification.Body, SecretPurposes.ProviderNotificationRawBody, id),
            clock.GetUtcNow());
        context.ProviderNotifications.Add(stored);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return answer;
        }

        signals.Notify(WorkerNames.ProviderNotifications);
        return answer;
    }

    [LoggerMessage(LogLevel.Warning, "A notification for connection {Connection} from {Address} could not be verified; it was not stored")]
    private static partial void LogUnverified(ILogger logger, string connection, string? address);
}

/// <summary>
/// Applies stored notifications (PAY-24): outcomes through the payment and refund rules by reference, hints by asking
/// the provider. References that are not Tahsil's are ignored; failures retry, and fail for good after ten attempts.
/// </summary>
public sealed partial class ProviderNotificationProcessing(
    ITahsilDbContext context,
    ProviderAccess providers,
    PaymentOutcomes payments,
    RefundOutcomes refunds,
    PaymentRequests requests,
    Attention attention,
    BillingClock clock,
    ILogger<ProviderNotificationProcessing> logger)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    public async Task ProcessAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        var notification = await context.ProviderNotifications.SingleOrDefaultAsync(row => row.Id == notificationId, cancellationToken);
        if (notification is not { Status: ProviderNotificationStatus.Pending })
        {
            return;
        }

        try
        {
            var connection = await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == notification.ConnectionId, cancellationToken);
            var applied = false;
            foreach (var update in NotificationPayload.Read(notification.Payload))
            {
                applied |= await ApplyAsync(connection, update, cancellationToken);
            }

            notification.Complete(applied, clock.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, exception, notificationId);
            context.DiscardChanges();
            var failing = await context.ProviderNotifications.SingleAsync(row => row.Id == notificationId, cancellationToken);
            var now = clock.GetUtcNow();
            if (failing.RecordFailure(Common.Redaction.Mask(exception.Message), now + Backoff[Math.Min(failing.Attempts, Backoff.Length - 1)]))
            {
                await attention.RaiseAsync(AttentionKind.NotificationFailed, AuditSubjects.ProviderNotification, failing.Id, null, new Dictionary<string, string>
                {
                    ["type"] = failing.Type,
                    ["error"] = failing.LastError ?? string.Empty,
                }, now, cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Applies one update; returns whether its reference is one of Tahsil's.</summary>
    private async Task<bool> ApplyAsync(ProviderConnection connection, StoredUpdate update, CancellationToken cancellationToken)
    {
        switch (update.Kind)
        {
            case StoredUpdate.Payment when update.PaymentOutcome is { } outcome:
                if (await PaymentAsync(connection, update.Reference, cancellationToken) is not { } payment)
                {
                    return false;
                }

                await payments.ApplyAsync(payment.Id, outcome, cancellationToken);
                return true;
            case StoredUpdate.CheckPayment:
                if (await PaymentAsync(connection, update.Reference, cancellationToken) is not { } checkedPayment)
                {
                    return false;
                }

                if (providers.For(connection) is { } call
                    && await call.Provider.FindPaymentAsync(call.Context, await requests.QueryAsync(checkedPayment, connection.Id, cancellationToken), cancellationToken)
                        is PaymentLookup.Found { Outcome.Status: ProviderStatus.Succeeded or ProviderStatus.Failed } found)
                {
                    await payments.ApplyAsync(checkedPayment.Id, found.Outcome, cancellationToken);
                }

                return true;
            case StoredUpdate.Refund when update.RefundOutcome is { } refundOutcome:
                if (await RefundAsync(connection, update.Reference, cancellationToken) is not { } refund)
                {
                    return false;
                }

                await refunds.ApplyAsync(refund.Refund.Id, refundOutcome, cancellationToken);
                return true;
            case StoredUpdate.CheckRefund:
                if (await RefundAsync(connection, update.Reference, cancellationToken) is not var (checkedRefund, refundPayment))
                {
                    return false;
                }

                if (providers.For(connection) is { } refundCall
                    && await refundCall.Provider.FindRefundAsync(
                        refundCall.Context,
                        new RefundQuery(checkedRefund.Reference, refundPayment.Reference, checkedRefund.ProviderRefundId, refundPayment.ProviderPaymentId, checkedRefund.CreatedAt),
                        cancellationToken) is RefundLookup.Found { Outcome.Status: ProviderRefundStatus.Succeeded or ProviderRefundStatus.Failed } refundFound)
                {
                    await refunds.ApplyAsync(checkedRefund.Id, refundFound.Outcome, cancellationToken);
                }

                return true;
            default:
                // Card updates arrive with payment methods' own lifecycle.
                return false;
        }
    }

    private Task<Payment?> PaymentAsync(ProviderConnection connection, string? reference, CancellationToken cancellationToken) =>
        context.Payments.AsNoTracking().SingleOrDefaultAsync(row => row.Reference == reference && row.ConnectionId == connection.Id, cancellationToken);

    private async Task<(Refund Refund, Payment Payment)?> RefundAsync(ProviderConnection connection, string? reference, CancellationToken cancellationToken) =>
        await context.Refunds.AsNoTracking()
            .Where(row => row.Reference == reference)
            .Join(context.Payments.AsNoTracking().Where(row => row.ConnectionId == connection.Id), refund => refund.PaymentId, payment => payment.Id, (refund, payment) => new { refund, payment })
            .SingleOrDefaultAsync(cancellationToken) is { } found
            ? (found.refund, found.payment)
            : null;

    [LoggerMessage(LogLevel.Warning, "Provider notification {NotificationId} could not be applied; it is retried later")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid notificationId);
}

/// <summary>One update of a stored notification, as JSON in its payload.</summary>
internal sealed record StoredUpdate(string Kind, string? Reference, PaymentOutcome? PaymentOutcome = null, RefundOutcome? RefundOutcome = null, string? Token = null, SavedCard? Card = null)
{
    public const string Payment = "payment";
    public const string Refund = "refund";
    public const string CheckPayment = "check_payment";
    public const string CheckRefund = "check_refund";
    public const string CardUpdated = "card_updated";
}

internal static class NotificationPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(IReadOnlyList<NotificationUpdate> updates) => JsonSerializer.Serialize(updates.Select(update => update switch
    {
        NotificationUpdate.Payment payment => new StoredUpdate(StoredUpdate.Payment, payment.Reference, PaymentOutcome: payment.Outcome),
        NotificationUpdate.Refund refund => new StoredUpdate(StoredUpdate.Refund, refund.Reference, RefundOutcome: refund.Outcome),
        NotificationUpdate.CheckPayment check => new StoredUpdate(StoredUpdate.CheckPayment, check.Reference),
        NotificationUpdate.CheckRefund check => new StoredUpdate(StoredUpdate.CheckRefund, check.Reference),
        NotificationUpdate.CardUpdated card => new StoredUpdate(StoredUpdate.CardUpdated, null, Token: card.Token, Card: card.Details),
        _ => throw new NotSupportedException($"Unknown notification update {update.GetType().Name}."),
    }).ToList(), Options);

    public static IReadOnlyList<StoredUpdate> Read(string payload) => JsonSerializer.Deserialize<List<StoredUpdate>>(payload, Options) ?? [];
}
