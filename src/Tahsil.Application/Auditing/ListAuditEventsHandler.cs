using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Auditing;

/// <summary>Filters the audit trail; every filter is optional and they combine. <c>From</c> is inclusive, <c>To</c> exclusive.</summary>
public sealed record ListAuditEventsQuery(
    string? Action = null,
    ActorKind? ActorType = null,
    string? ActorId = null,
    string? SubjectType = null,
    string? SubjectId = null,
    Guid? CustomerId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Cursor = null,
    int? Limit = null);

/// <summary>Reads the audit trail, newest first.</summary>
public sealed class ListAuditEventsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<AuditEvent, Guid> Newest = new("newest", auditEvent => auditEvent.Id, auditEvent => auditEvent.Id, Descending: true);

    /// <param name="query">The filters.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <param name="authorize">
    /// Whether to require <c>tahsil.audit:read</c>; a use case that already authorized its caller for the data, such as a
    /// customer's export, passes <see langword="false"/>.
    /// </param>
    public async Task<Result<Page<AuditEventResponse>>> HandleAsync(ListAuditEventsQuery query, CancellationToken cancellationToken, bool authorize = true)
    {
        if (authorize && authorizer.Require(TahsilPermissions.AuditRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var limit = query.Limit ?? Paging.DefaultLimit;
        if (!Paging.IsValidLimit(limit))
        {
            return CommonErrors.InvalidLimit();
        }

        var events = context.AuditEvents.AsNoTracking();
        if (query.Action is { Length: > 0 } action)
        {
            events = events.Where(auditEvent => auditEvent.Action == action);
        }

        if (query.ActorType is { } actorType)
        {
            var domainType = ToDomain(actorType);
            events = events.Where(auditEvent => auditEvent.ActorType == domainType);
        }

        if (query.ActorId is { Length: > 0 } actorId)
        {
            events = events.Where(auditEvent => auditEvent.ActorId == actorId);
        }

        if (query.SubjectType is { Length: > 0 } subjectType)
        {
            events = events.Where(auditEvent => auditEvent.SubjectType == subjectType);
        }

        if (query.SubjectId is { Length: > 0 } subjectId)
        {
            events = events.Where(auditEvent => auditEvent.SubjectId == subjectId);
        }

        if (query.CustomerId is { } customerId)
        {
            events = events.Where(auditEvent => auditEvent.CustomerId == customerId);
        }

        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            events = events.Where(auditEvent => auditEvent.OccurredAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            events = events.Where(auditEvent => auditEvent.OccurredAt < toUtc);
        }

        var page = await Newest.ReadAsync(events, query.Cursor, limit, cancellationToken);
        return page is null
            ? CommonErrors.InvalidCursor
            : new Page<AuditEventResponse>([.. page.Items.Select(ToResponse)], page.NextCursor);
    }

    private static ActorType ToDomain(ActorKind kind) => kind switch
    {
        ActorKind.ApiKey => ActorType.ApiKey,
        ActorKind.Admin => ActorType.Admin,
        ActorKind.Customer => ActorType.Customer,
        _ => ActorType.System,
    };

    private static AuditEventResponse ToResponse(AuditEvent auditEvent) => new(
        auditEvent.Id,
        auditEvent.OccurredAt,
        auditEvent.Action,
        new Actor(auditEvent.ActorType, auditEvent.ActorId).ToResponse(),
        auditEvent.SubjectType,
        auditEvent.SubjectId,
        auditEvent.CustomerId,
        auditEvent.IpAddress?.ToString(),
        auditEvent.UserAgent,
        auditEvent.CorrelationId,
        auditEvent.Data is null ? null : JsonSerializer.Deserialize<JsonElement>(auditEvent.Data));
}
