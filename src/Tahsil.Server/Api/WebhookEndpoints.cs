using System.ComponentModel;
using Tahsil.Application.Common;
using Tahsil.Application.Webhooks;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Webhook endpoints, their deliveries and the event catalog (EVT §4).</summary>
internal static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder api)
    {
        var webhooks = api.MapGroup("webhooks").WithTags("Webhooks");

        webhooks.MapGet("event-types", (ListEventTypesHandler handler) => handler.Handle().ToOk())
            .WithName("ListEventTypes").WithSummary("List the event types").RequirePermission(TahsilPermissions.WebhooksRead);

        webhooks.MapGet("endpoints", async (
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListWebhookEndpointsHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListWebhookEndpointsQuery(cursor, limit), cancellationToken)).ToOk())
            .WithName("ListWebhookEndpoints").WithSummary("List webhook endpoints").RequirePermission(TahsilPermissions.WebhooksRead);

        webhooks.MapPost("endpoints", async (CreateWebhookEndpointRequest request, CreateWebhookEndpointHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(created => Location(created.Endpoint.Id)))
            .WithName("CreateWebhookEndpoint").WithSummary("Create a webhook endpoint")
            .WithDescription("Returns the signing secret once. Events are signed as Standard Webhooks describes; an empty list of event types means all.")
            .ProducesValidationProblem().RequirePermission(TahsilPermissions.WebhooksWrite);

        webhooks.MapGet("endpoints/{id:guid}", async (Guid id, GetWebhookEndpointHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("GetWebhookEndpoint").WithSummary("Get a webhook endpoint").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.WebhooksRead);

        webhooks.MapPatch("endpoints/{id:guid}", async (Guid id, UpdateWebhookEndpointRequest request, UpdateWebhookEndpointHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(id, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateWebhookEndpoint").WithSummary("Update a webhook endpoint")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.WebhooksWrite);

        webhooks.MapDelete("endpoints/{id:guid}", async (Guid id, DeleteWebhookEndpointHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToNoContent())
            .WithName("DeleteWebhookEndpoint").WithSummary("Delete a webhook endpoint")
            .WithDescription("Deletes the endpoint with its delivery log; deliveries under way are dropped.")
            .ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.WebhooksWrite);

        webhooks.MapPost("endpoints/{id:guid}/secret", async (Guid id, RotateWebhookSecretHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("RotateWebhookSecret").WithSummary("Rotate a webhook endpoint's secret")
            .WithDescription("Returns the new secret once. Until `previousSecretExpiresAt`, deliveries carry a signature with each secret, so receivers can switch without losing events.")
            .ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.WebhooksWrite);

        webhooks.MapPost("endpoints/{id:guid}/test", async (Guid id, SendTestWebhookHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("SendTestWebhook").WithSummary("Send a test event")
            .WithDescription("Sends a `webhook.test` event to the endpoint, even when it is disabled.")
            .ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.WebhooksWrite);

        webhooks.MapGet("deliveries", async (
            [Description("Only deliveries to this endpoint.")] Guid? endpoint,
            [Description("`pending`, `succeeded` or `failed`.")] string? status,
            [Description("Only deliveries of this event type.")] string? eventType,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListWebhookDeliveriesHandler handler,
            CancellationToken cancellationToken) =>
            QueryValues.TryParseEnum<WebhookDeliveryStatus>(status, out var statusFilter)
                ? (await handler.HandleAsync(new ListWebhookDeliveriesQuery(endpoint, statusFilter, eventType, cursor, limit), cancellationToken)).ToOk()
                : ApiResults.Problem(CommonErrors.InvalidParameter(nameof(status))))
            .WithName("ListWebhookDeliveries").WithSummary("List webhook deliveries").WithDescription("Newest first.")
            .RequirePermission(TahsilPermissions.WebhooksRead);

        webhooks.MapGet("deliveries/{id:guid}", async (Guid id, GetWebhookDeliveryHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("GetWebhookDelivery").WithSummary("Get a webhook delivery").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.WebhooksRead);

        webhooks.MapPost("deliveries/{id:guid}/redeliver", async (Guid id, RedeliverWebhookHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("RedeliverWebhook").WithSummary("Send a delivery again")
            .WithDescription("Sends the event again with the same event ID, as a fresh series of attempts.")
            .ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.WebhooksWrite);
        return api;
    }

    public static IServiceCollection AddWebhookResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.WebhookEndpoint, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetWebhookEndpointHandler>().HandleAsync(id, http.RequestAborted)).ToCreated(http.Response, endpoint => Location(endpoint.Id))));

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/webhooks/endpoints/{id}";
}
