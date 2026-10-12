using Tahsil.Application.Portal;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Portal sessions: the application's way to send an authenticated customer to the customer portal (PRT §1).</summary>
internal static class PortalEndpoints
{
    public static IEndpointRouteBuilder MapPortalEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGroup("portal-sessions").WithTags("Customer portal")
            .MapPost(string.Empty, async (CreatePortalSessionRequest request, CreatePortalSessionHandler handler, CancellationToken cancellationToken) =>
                (await handler.HandleAsync(request, cancellationToken)).ToCreated(session => session.Url.AbsoluteUri))
            .WithName("CreatePortalSession").WithSummary("Create a portal session")
            .WithDescription("""
                For a customer the application has authenticated: redirect them to `url`, where they manage their subscriptions, card, invoices and billing details. The session works for that customer alone, as often as they like until `expiresAt` (`Tahsil:Portal:SessionLifetime`, an hour by default). `url` is given now only; create another session when it is lost. `flow` opens the portal on one task.
                """)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CheckoutWrite);
        return api;
    }
}
