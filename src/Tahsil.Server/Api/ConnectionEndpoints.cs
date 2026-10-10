using System.ComponentModel;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Payment providers, the connections to them and which connection serves each currency (PAY-1 to PAY-6).</summary>
internal static class ConnectionEndpoints
{
    private const string Collection = "provider-connections";

    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("providers", (ListProvidersHandler handler) => handler.Handle().ToOk())
            .WithTags("Provider connections").WithName("ListProviders").WithSummary("List payment providers")
            .WithDescription("The providers this installation has adapters for, with the credentials and settings their connections take and what they can do.")
            .RequirePermission(TahsilPermissions.ConnectionsRead);

        var connections = api.MapGroup(Collection).WithTags("Provider connections");
        MapRouting(connections);

        connections.MapGet(string.Empty, async (
            [Description("Only connections to this provider.")] string? provider,
            [Description("Only enabled (true) or disabled (false) connections.")] bool? enabled,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListConnectionsHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListConnectionsQuery(provider, enabled, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListConnections").WithSummary("List provider connections").RequirePermission(TahsilPermissions.ConnectionsRead);

        connections.MapPost(string.Empty, async (CreateConnectionRequest request, CreateConnectionHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, connection => Location(connection.Id)))
            .WithName("CreateConnection").WithSummary("Create a provider connection")
            .WithDescription("Credentials are validated against the provider's fields, stored encrypted and never returned. Test mode takes sandbox connections, Live mode production ones.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.ConnectionsWrite);

        connections.MapGet("{idOrKey}", async (string idOrKey, GetConnectionHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToOk(response))
            .WithName("GetConnection").WithSummary("Get a provider connection").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.ConnectionsRead);

        connections.MapPatch("{idOrKey}", async (string idOrKey, UpdateConnectionRequest request, UpdateConnectionHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(idOrKey, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateConnection").WithSummary("Update a provider connection")
            .WithDescription("Credentials and settings merge with the current ones. A disabled connection takes no new payments, and renewals of its cards fail until it is enabled again.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.ConnectionsWrite);

        connections.MapDelete("{idOrKey}", async (string idOrKey, DeleteConnectionHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToNoContent())
            .WithName("DeleteConnection").WithSummary("Delete a provider connection")
            .WithDescription("Only while the connection has no payments or payment methods; otherwise disable it.")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.ConnectionsWrite);

        connections.MapPost("{idOrKey}/test", async (string idOrKey, TestConnectionHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToOk(response))
            .WithName("TestConnection").WithSummary("Test a provider connection")
            .WithDescription("Asks the provider whether the credentials work and records the result as `lastTest`.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.ConnectionsWrite);
        return api;
    }

    public static IServiceCollection AddConnectionResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.Connection, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetConnectionHandler>().HandleAsync(id.ToString(), http.RequestAborted))
            .ToCreated(http.Response, connection => Location(connection.Id))));

    private static void MapRouting(RouteGroupBuilder connections)
    {
        connections.MapGet("routing", async (GetRoutingHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(cancellationToken)).ToOk())
            .WithName("GetRouting").WithSummary("Get the connection of each currency")
            .WithDescription("Currency codes and the keys of the connections that take customer-present payments in them.")
            .RequirePermission(TahsilPermissions.ConnectionsRead);

        connections.MapPut("routing", async (IReadOnlyDictionary<string, string> routing, UpdateRoutingHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(routing, cancellationToken)).ToOk())
            .WithName("UpdateRouting").WithSummary("Set the connection of each currency")
            .WithDescription("Replaces the routing, such as `{ \"TRY\": \"iyzico-main\", \"USD\": \"stripe-main\" }`. A connection serves only currencies its provider supports, and only while enabled; saved cards are always charged through the connection that saved them.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.ConnectionsWrite);
    }

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/{Collection}/{id}";
}
