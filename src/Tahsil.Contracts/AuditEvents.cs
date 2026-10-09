using System.Text.Json;

namespace Tahsil.Contracts;

/// <summary>
/// An action recorded in the audit trail. <c>action</c> reads <c>resource.past_tense_verb</c>, such as
/// <c>api_key.revoked</c>. People are referenced by ID only, so the trail outlives their personal data.
/// </summary>
public sealed record AuditEventResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    ActorResponse Actor,
    string? SubjectType,
    string? SubjectId,
    Guid? CustomerId,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId,
    JsonElement? Data);
