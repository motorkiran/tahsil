using Tahsil.Application.Usage;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Usage reporting for metered prices (USG-1 to USG-6).</summary>
internal static class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("usage-events", async (ReportUsageRequest request, ReportUsageHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToOk())
            .WithTags("Usage")
            .WithName("ReportUsage").WithSummary("Report usage events")
            .WithDescription("Stores a batch of usage events for metered prices. Each event is accepted, a duplicate or rejected by itself, and the answer says which: an event whose `id` is stored already is a duplicate whatever it says, so retry a batch with the same IDs. An event of a period that is already invoiced is rejected with `usage_event.period_closed`. This endpoint has a rate limit of its own.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(ApiRateLimiting.UsagePolicyName)
            .RequirePermission(TahsilPermissions.UsageWrite);
        return api;
    }
}
