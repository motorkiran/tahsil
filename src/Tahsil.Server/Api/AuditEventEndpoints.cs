using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Tahsil.Application.Auditing;
using Tahsil.Application.Common;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

internal static class AuditEventEndpoints
{
    public static IEndpointRouteBuilder MapAuditEventEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("audit-events", ListAsync)
            .WithTags("Audit events")
            .WithName("ListAuditEvents")
            .WithSummary("Query the audit trail")
            .WithDescription("Lists audit events, newest first. Every filter is optional, and filters combine.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequirePermission(TahsilPermissions.AuditRead);

        return api;
    }

    private static async Task<Results<Ok<Page<AuditEventResponse>>, ProblemHttpResult>> ListAsync(
        [Description("An action such as `api_key.revoked`.")] string? action,
        [Description("`system`, `api_key`, `admin` or `customer`.")] string? actorType,
        [Description("An API key ID, administrator subject or customer ID.")] string? actorId,
        [Description("What was acted on, such as `customer` or `invoice`.")] string? subjectType,
        [Description("The ID of what was acted on.")] string? subjectId,
        [Description("Events concerning this customer.")] Guid? customerId,
        [Description("Events at or after this time (ISO 8601).")] DateTimeOffset? from,
        [Description("Events before this time (ISO 8601).")] DateTimeOffset? to,
        [Description("The `nextCursor` of the previous page.")] string? cursor,
        [Description("Page size, 50 by default and at most 200.")] int? limit,
        ListAuditEventsHandler handler,
        CancellationToken cancellationToken)
    {
        if (!QueryValues.TryParseEnum<ActorKind>(actorType, out var actorTypeFilter))
        {
            return ApiResults.Problem(CommonErrors.InvalidParameter(nameof(actorType)));
        }

        var query = new ListAuditEventsQuery(action, actorTypeFilter, actorId, subjectType, subjectId, customerId, from, to, cursor, limit);
        return (await handler.HandleAsync(query, cancellationToken)).ToOk();
    }
}
