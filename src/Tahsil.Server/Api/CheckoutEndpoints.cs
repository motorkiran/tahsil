using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Server.Api;

/// <summary>Checkout sessions: hosted pages that sell to one customer (CHK §1, §7).</summary>
internal static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder api)
    {
        var sessions = api.MapGroup("checkout-sessions").WithTags("Checkout");
        sessions.MapPost(string.Empty, async (CreateCheckoutSessionRequest request, CreateCheckoutSessionHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(session => Location(session.Id)))
            .WithName("CreateCheckoutSession").WithSummary("Create a checkout session")
            .WithDescription("Prices and the amount due are computed now and shown on the page as the `quote`; send the customer to `url`. The session expires unpaid after `expiresIn` (24 hours by default).")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CheckoutWrite);

        sessions.MapGet(string.Empty, async (
            [Description("`open`, `complete` or `expired`.")] string? status,
            [Description("Only sessions of this customer.")] Guid? customer,
            [Description("Only sessions with this client reference ID.")] string? clientReferenceId,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListCheckoutSessionsHandler handler,
            CancellationToken cancellationToken) =>
            QueryValues.TryParseEnum<CheckoutStatus>(status, out var statusFilter)
                ? (await handler.HandleAsync(new ListCheckoutSessionsQuery(statusFilter, customer, clientReferenceId, cursor, limit), cancellationToken)).ToOk()
                : ApiResults.Problem(CommonErrors.InvalidParameter(nameof(status))))
            .WithName("ListCheckoutSessions").WithSummary("List checkout sessions").WithDescription("Newest first.")
            .RequirePermission(TahsilPermissions.SubscriptionsRead);

        sessions.MapGet("{id:guid}", async (Guid id, GetCheckoutSessionHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("GetCheckoutSession").WithSummary("Get a checkout session")
            .WithDescription("`url` is given while the session is open.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.SubscriptionsRead);

        sessions.MapPost("{id:guid}/expire", async (Guid id, ExpireCheckoutSessionHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("ExpireCheckoutSession").WithSummary("Expire a checkout session")
            .WithDescription("Ends an open session so it can no longer be paid; `checkout.expired` is sent.")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CheckoutWrite);
        return api;
    }

    /// <summary>A retried creation answers with the session it created, whatever the caller may read otherwise.</summary>
    public static IServiceCollection AddCheckoutResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.CheckoutSession, async (http, id) =>
        {
            var session = await http.RequestServices.GetRequiredService<ITahsilDbContext>().CheckoutSessions.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == id, http.RequestAborted);
            return Result.Success(http.RequestServices.GetRequiredService<CheckoutMapper>().ToResponse(session)).ToCreated(response => Location(response.Id));
        }));

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/checkout-sessions/{id}";
}
