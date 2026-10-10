using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Webhooks;

namespace Tahsil.Application.Webhooks;

/// <summary>An event to send to every endpoint that receives its type; written to the outbox with the change it reports (EVT-9).</summary>
[OutboxMessage("webhooks.event")]
public sealed record WebhookEventPublished(Guid EventId, string Type, string Payload);

/// <summary>Turns an event into one delivery per enabled endpoint that receives it, once per event.</summary>
public sealed class WebhookFanOut(ITahsilDbContext context, IWorkerSignals signals, BillingClock clock) : IOutboxMessageHandler<WebhookEventPublished>
{
    public const string WorkerName = "webhooks";

    public async Task HandleAsync(WebhookEventPublished message, CancellationToken cancellationToken)
    {
        // Delivery from the outbox is at least once; a repeat must not send the event twice.
        if (await context.WebhookDeliveries.AnyAsync(delivery => delivery.EventId == message.EventId, cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var endpoints = await context.WebhookEndpoints.AsNoTracking().Where(endpoint => endpoint.Enabled).ToListAsync(cancellationToken);
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Receives(message.Type)))
        {
            context.WebhookDeliveries.Add(WebhookDelivery.Create(endpoint.Id, message.EventId, message.Type, message.Payload, now));
        }

        await context.SaveChangesAsync(cancellationToken);
        signals.Notify(WorkerName);
    }
}

/// <summary>Builds webhook events from audited changes: each event is the audit event of its change (EVT-4).</summary>
public static class WebhookEvents
{
    /// <summary>The type of the events that only try an endpoint out.</summary>
    public const string TestEventType = TahsilEventTypes.Test;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The event for an audit event whose action is an event type of the catalog, or <see langword="null"/>.</summary>
    public static WebhookEventPublished? From(AuditEvent auditEvent, TahsilMode mode)
    {
        if (!TahsilEventTypes.All.Contains(auditEvent.Action) || auditEvent.SubjectType is null || auditEvent.SubjectId is null)
        {
            return null;
        }

        var details = auditEvent.Data is { } data ? JsonDocument.Parse(data).RootElement.Clone() : (JsonElement?)null;
        var webhookEvent = new WebhookEvent(
            auditEvent.Action,
            auditEvent.OccurredAt,
            ModeName(mode),
            new WebhookEventData(auditEvent.SubjectType, auditEvent.SubjectId, auditEvent.CustomerId, ActorOf(new Actor(auditEvent.ActorType, auditEvent.ActorId)), details));
        return new WebhookEventPublished(auditEvent.Id, auditEvent.Action, Serialize(webhookEvent));
    }

    internal static WebhookEventActor ActorOf(Actor actor) => new(JsonNamingPolicy.SnakeCaseLower.ConvertName(actor.Type.ToString()), actor.Id);

    internal static string ModeName(TahsilMode mode) => mode == TahsilMode.Live ? "live" : "test";

    internal static string Serialize(WebhookEvent webhookEvent) => JsonSerializer.Serialize(webhookEvent, Options);
}
