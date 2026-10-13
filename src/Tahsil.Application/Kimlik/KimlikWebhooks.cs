using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Subscriptions;
using Tahsil.Application.Webhooks;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using KimlikSubscriber = Tahsil.Domain.Customers.KimlikSubscriber;
using KimlikSubscriberType = Tahsil.Domain.Customers.KimlikSubscriberType;
using SubscriptionStatus = Tahsil.Domain.Subscriptions.SubscriptionStatus;

namespace Tahsil.Application.Kimlik;

/// <summary>A webhook from Kimlik that Tahsil acts on, verified and kept to be handled after the commit (KML-22).</summary>
[OutboxMessage("kimlik.event_received")]
public sealed record KimlikEventReceived(string Type, string SubjectId, string ActorType, string? ActorId);

/// <summary>
/// Receives Kimlik's webhooks at <c>POST /webhooks/kimlik</c> (KML-22): verified with Standard Webhooks and the
/// endpoint's secret, within five minutes of their timestamp, as Kimlik's own verifier does. The events Tahsil acts on
/// are queued; others are acknowledged and ignored (KML-24).
/// </summary>
public sealed class KimlikWebhooks(ITahsilDbContext context, IOutbox outbox, IOptions<KimlikOptions> options, TimeProvider time)
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "user.deleted", "organization.deleted", "subscription.created", "subscription.updated", "subscription.canceled", "subscription.expired",
    };

    /// <summary>Whether the connector takes webhooks; without it, the endpoint does not exist.</summary>
    public bool IsEnabled => options.Value.Enabled;

    /// <summary>Whether the request is a webhook from Kimlik signed with the endpoint's secret; if so, it is accepted.</summary>
    public async Task<bool> ReceiveAsync(string? id, string? timestamp, string? signatures, string body, CancellationToken cancellationToken)
    {
        if (!Verify(id, timestamp, signatures, body))
        {
            return false;
        }

        if (JsonSerializer.Deserialize<WebhookEvent>(body, Json) is { Data: { SubjectId: { } subjectId, Actor: { } actor } } received && Handled.Contains(received.Type))
        {
            outbox.Enqueue(new KimlikEventReceived(received.Type, subjectId, actor.Type, actor.Id));
            await context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private bool Verify(string? id, string? timestamp, string? signatures, string body)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatures)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || (time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(WebhookSecrets.KeyOf(options.Value.WebhookSecret!), Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));

        // The header can carry several signatures, such as while Kimlik replaces the secret.
        return signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(signature =>
        {
            if (!signature.StartsWith("v1,", StringComparison.Ordinal))
            {
                return false;
            }

            var candidate = new byte[64];
            return Convert.TryFromBase64String(signature[3..], candidate, out var length) && CryptographicOperations.FixedTimeEquals(candidate.AsSpan(0, length), expected);
        });
    }

    private sealed record WebhookEvent(string Type, WebhookEventData? Data);

    private sealed record WebhookEventData(string? SubjectId, WebhookEventActor? Actor);

    private sealed record WebhookEventActor(string Type, string? Id);
}

/// <summary>
/// Acts on Kimlik's webhooks: a deleted subscriber ends its customer's subscriptions (KML-23), and a change made in
/// Kimlik to a mirrored subscription by anyone but Tahsil is undone by a sync (KML-21). Repeats change nothing (KML-24).
/// </summary>
public sealed class KimlikEvents(
    ITahsilDbContext context,
    IKimlikClient kimlik,
    CancelSubscriptionHandler cancel,
    IAuditLog audit,
    IOutbox outbox,
    IOptions<KimlikOptions> options,
    BillingClock clock) : IOutboxMessageHandler<KimlikEventReceived>
{
    private static readonly string[] Unlinked = ["kimlikSubscriber"];

    public async Task HandleAsync(KimlikEventReceived message, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || !Guid.TryParse(message.SubjectId, out var subjectId))
        {
            return;
        }

        switch (message.Type)
        {
            case "user.deleted":
                await DeletedAsync(new KimlikSubscriber(KimlikSubscriberType.User, subjectId), cancellationToken);
                break;
            case "organization.deleted":
                await DeletedAsync(new KimlikSubscriber(KimlikSubscriberType.Organization, subjectId), cancellationToken);
                break;

            // Tahsil's own changes come back as events too.
            case var _ when message.ActorType != "client" || message.ActorId != options.Value.ClientId:
                await DriftedAsync(subjectId, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Kimlik deleted the subscriber, and its subscriptions with it, silently: the customer's subscriptions end now, without
    /// a refund or a final invoice, and the customer, unlinked, keeps its billing history (KML-23).
    /// </summary>
    private async Task DeletedAsync(KimlikSubscriber subscriber, CancellationToken cancellationToken)
    {
        if (await context.Customers.SingleOrDefaultAsync(
                customer => customer.KimlikSubscriberType == subscriber.Type && customer.KimlikSubscriberId == subscriber.Id, cancellationToken)
            is not { } customer)
        {
            return;
        }

        var running = await context.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.CustomerId == customer.Id
                && subscription.Status != SubscriptionStatus.Canceled && subscription.Status != SubscriptionStatus.IncompleteExpired)
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in running)
        {
            var canceled = await cancel.HandleAsync(
                id, new CancelSubscriptionRequest { At = "now", Reason = CancellationReason.KimlikSubscriberDeleted, InvoiceNow = false }, null, cancellationToken);
            if (canceled.IsFailure)
            {
                throw new InvalidOperationException($"Subscription {id} of a deleted Kimlik subscriber could not be canceled: {canceled.Error!.Code}.");
            }
        }

        customer.UnlinkKimlik(clock.GetUtcNow());
        audit.Record(
            AuditActions.CustomerUpdated,
            AuditSubject.Of(AuditSubjects.Customer, customer.Id),
            new Dictionary<string, object?> { ["changed"] = Unlinked, ["reason"] = "kimlik_subscriber_deleted" },
            customerId: customer.Id);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Someone changed a mirrored subscription in Kimlik: a sync restores Tahsil's state (KML-21).</summary>
    private async Task DriftedAsync(Guid kimlikSubscriptionId, CancellationToken cancellationToken)
    {
        var subscriptionId = await context.KimlikMirrors.Where(mirror => mirror.KimlikSubscriptionId == kimlikSubscriptionId)
            .Select(mirror => (Guid?)mirror.SubscriptionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (subscriptionId is null)
        {
            // Not one Tahsil knows as its own, unless it names a Tahsil subscription, such as one created while Tahsil's request timed out.
            var read = await kimlik.GetSubscriptionAsync(kimlikSubscriptionId, cancellationToken);
            if (read.IsFailure || !Guid.TryParse(read.Value.ExternalReference, out var referenced)
                || !await context.Subscriptions.AnyAsync(subscription => subscription.Id == referenced, cancellationToken))
            {
                return;
            }

            subscriptionId = referenced;
        }

        var subscription = await context.Subscriptions.AsNoTracking().SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        audit.Record(
            AuditActions.KimlikDriftCorrected,
            AuditSubject.Of(AuditSubjects.Subscription, subscription.Id),
            new Dictionary<string, object?> { ["kimlikSubscriptionId"] = kimlikSubscriptionId },
            customerId: subscription.CustomerId);
        outbox.Enqueue(new SyncKimlikSubscription(subscription.Id));
        await context.SaveChangesAsync(cancellationToken);
    }
}
