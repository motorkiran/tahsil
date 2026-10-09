using System.Net;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Auditing;

/// <summary>
/// An immutable record of an action. Audit events reference people by ID only, so erasing a customer's personal
/// data does not rewrite the trail.
/// </summary>
public sealed class AuditEvent
{
    public const int ActionMaxLength = 64;
    public const int ReferenceMaxLength = 128;
    public const int UserAgentMaxLength = 512;

    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>What happened, as <c>resource.past_tense_verb</c>.</summary>
    public required string Action { get; init; }

    public required ActorType ActorType { get; init; }

    public string? ActorId { get; init; }

    public string? SubjectType { get; init; }

    public string? SubjectId { get; init; }

    /// <summary>The customer the action concerns, so a customer's trail can be read as a whole.</summary>
    public Guid? CustomerId { get; init; }

    public IPAddress? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Additional details as a JSON object.</summary>
    public string? Data { get; init; }
}
