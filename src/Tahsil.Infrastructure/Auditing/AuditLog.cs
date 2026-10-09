using System.Text.Json;
using Tahsil.Application.Common;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Auditing;

/// <summary>Records audit events in the current unit of work, at the billing clock's time.</summary>
internal sealed class AuditLog(TahsilDbContext context, IRequestContext request, BillingClock clock) : IAuditLog
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

        context.AuditEvents.Add(new AuditEvent
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
        });
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
