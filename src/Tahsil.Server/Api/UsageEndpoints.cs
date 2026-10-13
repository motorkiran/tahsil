using System.ComponentModel;
using Tahsil.Application.Common;
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
        api.MapGet("customers/{id:guid}/usage", async (
            Guid id,
            [Description("The meter, by ID or key.")] string? meter,
            [Description("The start of the range; 30 days before `to` by default, or 12 months for months.")] DateTimeOffset? from,
            [Description("The end of the range, which is not part of it; now by default.")] DateTimeOffset? to,
            [Description("`day` (the default) or `month`, in the billing time zone.")] string? interval,
            GetCustomerUsageHandler handler,
            CancellationToken cancellationToken) =>
            QueryValues.TryParseEnum<UsageInterval>(interval, out var parsed)
                ? (await handler.HandleAsync(id, new CustomerUsageQuery(meter, from, to, parsed ?? UsageInterval.Day), cancellationToken)).ToOk()
                : ApiResults.Problem(CommonErrors.InvalidParameter(nameof(interval))))
            .WithTags("Usage")
            .WithName("GetCustomerUsage").WithSummary("Get a customer's usage on a meter")
            .WithDescription("The quantity of the range and of each day or month in it, each aggregated the meter's way, whether a subscription bills the usage or not. At most 400 intervals.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.UsageRead);

        api.MapGet("subscriptions/{id:guid}/usage", async (Guid id, GetSubscriptionUsageHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithTags("Usage")
            .WithName("GetSubscriptionUsage").WithSummary("Get a subscription's usage in its current period")
            .WithDescription("For each metered item: the quantity used in the current period so far and the amount it would bill now, before discounts and tax that is added.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.UsageRead);
        return api;
    }
}
