using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Subscriptions;
using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Server.Api;

/// <summary>Subscriptions: recurring purchases of recurring prices (SUB §8).</summary>
internal static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder api)
    {
        var subscriptions = api.MapGroup("subscriptions").WithTags("Subscriptions");
        subscriptions.MapPost(string.Empty, async (CreateSubscriptionRequest request, CreateSubscriptionHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(subscription => Location(subscription.Id)))
            .WithName("CreateSubscription").WithSummary("Create a subscription")
            .WithDescription("""
                With a trial, the subscription starts `trialing` and nothing is charged. Otherwise its first invoice is finalized at once: charged to the card during the request with `charge_automatically` (`active` when paid; `incomplete` when declined, with `latestInvoice.hostedUrl` for the customer to pay within a day), or sent with `send_invoice` (`active` at once).
                """)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.SubscriptionsWrite);

        subscriptions.MapGet(string.Empty, async (
            [Description("Only subscriptions of this customer.")] Guid? customer,
            [Description("`incomplete`, `incomplete_expired`, `trialing`, `active`, `past_due` or `canceled`.")] string? status,
            [Description("Only subscriptions with this price, by ID or lookup key.")] string? price,
            [Description("Only subscriptions with a price of this product, by ID or key.")] string? product,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListSubscriptionsHandler handler,
            CancellationToken cancellationToken) =>
            QueryValues.TryParseEnum<SubscriptionStatus>(status, out var statusFilter)
                ? (await handler.HandleAsync(new ListSubscriptionsQuery(customer, statusFilter, price, product, cursor, limit), cancellationToken)).ToOk()
                : ApiResults.Problem(CommonErrors.InvalidParameter(nameof(status))))
            .WithName("ListSubscriptions").WithSummary("List subscriptions").WithDescription("Newest first.")
            .RequirePermission(TahsilPermissions.SubscriptionsRead);

        subscriptions.MapGet("{id:guid}", async (Guid id, GetSubscriptionHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("GetSubscription").WithSummary("Get a subscription")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.SubscriptionsRead);
        return api;
    }

    /// <summary>A retried creation answers with the subscription it created, as it stands now.</summary>
    public static IServiceCollection AddSubscriptionResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.Subscription, async (http, id) =>
        {
            var subscription = await http.RequestServices.GetRequiredService<ITahsilDbContext>().Subscriptions.AsNoTracking()
                .Include(candidate => candidate.Items).SingleAsync(candidate => candidate.Id == id, http.RequestAborted);
            var response = await http.RequestServices.GetRequiredService<SubscriptionMapper>().ToResponseAsync(subscription, http.RequestAborted);
            return Result.Success(response).ToCreated(created => Location(created.Id));
        }));

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/subscriptions/{id}";
}
