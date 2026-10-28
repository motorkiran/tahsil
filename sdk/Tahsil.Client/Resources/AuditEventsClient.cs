using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>The audit trail. Requires <c>tahsil.audit:read</c>.</summary>
public sealed class AuditEventsClient
{
    private readonly TahsilHttp _http;

    internal AuditEventsClient(TahsilHttp http) => _http = http;

    /// <summary>Query the audit trail. Lists audit events, newest first. Every filter is optional, and filters combine.</summary>
    /// <param name="action">An action such as <c>api_key.revoked</c>.</param>
    /// <param name="actorType"><c>system</c>, <c>api_key</c>, <c>admin</c> or <c>customer</c>.</param>
    /// <param name="actorId">An API key ID, administrator subject or customer ID.</param>
    /// <param name="subjectType">What was acted on, such as <c>customer</c> or <c>invoice</c>.</param>
    /// <param name="subjectId">The ID of what was acted on.</param>
    /// <param name="customerId">Events concerning this customer.</param>
    /// <param name="from">Events at or after this time (ISO 8601).</param>
    /// <param name="to">Events before this time (ISO 8601).</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<AuditEventResponse>> ListAsync(string? action = null, ActorKind? actorType = null, string? actorId = null, string? subjectType = null, string? subjectId = null, Guid? customerId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<AuditEventResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("audit-events", ("action", action), ("actorType", actorType), ("actorId", actorId), ("subjectType", subjectType), ("subjectId", subjectId), ("customerId", customerId), ("from", from), ("to", to), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<AuditEventResponse> ListAllAsync(string? action = null, ActorKind? actorType = null, string? actorId = null, string? subjectType = null, string? subjectId = null, Guid? customerId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(action, actorType, actorId, subjectType, subjectId, customerId, from, to, cursor, limit, cancellationToken: cancellationToken), cancellationToken);
}
