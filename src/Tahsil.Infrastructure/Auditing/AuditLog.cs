using System.Text.Json;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Webhooks;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Auditing;

/// <summary>Records audit events in the current unit of work, at the billing clock's time.</summary>
/// <remarks>Audit events whose action is an event type of the catalog are also queued as webhooks (EVT-4).</remarks>
internal sealed class AuditLog(TahsilDbContext context, IRequestContext request, IOutbox outbox, IOptions<ServerOptions> server, BillingClock clock) : IAuditLog
{
    public void Record(
        string action,
        AuditSubject? subject = null,
        IReadOnlyDictionary<string, object?>? data = null,
        Actor? actor = null,
        Guid? customerId = null)
    {
        actor ??= request.Caller.ToActor();
        var now = clock.GetUtcNow();

        var auditEvent = new AuditEvent
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            Action = action,
            ActorType = actor.Type,
            ActorId = actor.Id,
            SubjectType = subject?.Type,
            SubjectId = subject?.Id,
            CustomerId = customerId,
            IpAddress = request.IpAddress,
            UserAgent = Truncate(request.UserAgent, AuditEvent.UserAgentMaxLength),
            CorrelationId = Truncate(request.CorrelationId, AuditEvent.ReferenceMaxLength),
            Data = data is null ? null : JsonSerializer.Serialize(data, StorageJson.Options),
        };
        context.AuditEvents.Add(auditEvent);
        if (WebhookEvents.From(auditEvent, server.Value.Mode) is { } webhookEvent)
        {
            outbox.Enqueue(webhookEvent);
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
