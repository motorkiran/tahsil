using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Subscriptions;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Subscriptions: recurring purchases of recurring prices (SUB §8).</summary>
internal static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder api)
    {
        var subscriptions = api.MapGroup("subscriptions").WithTags("Subscriptions");
        subscriptions.MapPost(string.Empty, async (CreateSubscriptionRequest request, CreateSubscriptionHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, subscription => Location(subscription.Id)))
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

        subscriptions.MapGet("{id:guid}", async (Guid id, GetSubscriptionHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("GetSubscription").WithSummary("Get a subscription")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.SubscriptionsRead);

        subscriptions.MapPatch("{id:guid}", async (Guid id, UpdateSubscriptionRequest request, UpdateSubscriptionHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(id, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateSubscription").WithSummary("Change a subscription")
            .WithDescription("""
                `items` replaces the item set. Changed `now` (the default), it is prorated by `prorationBehavior`: `always_invoice` bills the difference at once and charges it like a first invoice (a credit goes to the customer's balance), `create_prorations` leaves it for the next renewal, `none` bills nothing until then. With `effective: period_end` (and optionally a later renewal as `effectiveAt`) it is stored as `scheduledChange` and applied at that renewal. Prices with another interval restart the period now. A higher price for an individual customer must be scheduled at least `Tahsil:Subscriptions:PriceIncreaseNotice` ahead.
                """)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.SubscriptionsWrite);

        subscriptions.MapPost("{id:guid}/preview", async (Guid id, UpdateSubscriptionRequest request, PreviewSubscriptionChangeHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, request, cancellationToken)).ToOk())
            .WithName("PreviewSubscriptionChange").WithSummary("Preview a change of a subscription")
            .WithDescription("Takes the body of a change of `items` and returns the invoice it would create now (lines, totals, tax and the balance it would apply), the lines it would leave for the next renewal, and the next renewal, without changing anything.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.SubscriptionsRead);
        return api;
    }

    /// <summary>A retried creation answers with the subscription it created, as it stands now.</summary>
    public static IServiceCollection AddSubscriptionResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.Subscription, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetSubscriptionHandler>().HandleAsync(id, http.RequestAborted))
            .ToCreated(http.Response, subscription => Location(subscription.Id))));

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/subscriptions/{id}";
}
