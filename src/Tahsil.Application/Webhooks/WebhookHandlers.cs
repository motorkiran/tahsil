using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Webhooks;
using ContractStatus = Tahsil.Contracts.WebhookDeliveryStatus;
using DomainStatus = Tahsil.Domain.Webhooks.WebhookDeliveryStatus;

namespace Tahsil.Application.Webhooks;

public sealed class ListEventTypesHandler(IAuthorizer authorizer)
{
    public Result<Page<EventTypeResponse>> Handle()
    {
        if (authorizer.Require(TahsilPermissions.WebhooksRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return new Page<EventTypeResponse>([.. TahsilEventTypes.Descriptions.OrderBy(type => type.Key, StringComparer.Ordinal).Select(type => new EventTypeResponse(type.Key, type.Value))], null);
    }
}

public sealed record ListWebhookEndpointsQuery(string? Cursor = null, int? Limit = null);

public sealed class ListWebhookEndpointsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<WebhookEndpoint, Guid> ByCreation = new("created", endpoint => endpoint.Id, endpoint => endpoint.Id);

    public async Task<Result<Page<WebhookEndpointResponse>>> HandleAsync(ListWebhookEndpointsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var page = await ByCreation.ReadAsync(context.WebhookEndpoints.AsNoTracking(), query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<WebhookEndpointResponse>([.. page.Items.Select(WebhookMapper.ToResponse)], page.NextCursor);
    }
}

public sealed class GetWebhookEndpointHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<WebhookEndpointResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(endpoint => endpoint.Id == id, cancellationToken) is { } endpoint
            ? new Versioned<WebhookEndpointResponse>(WebhookMapper.ToResponse(endpoint), endpoint.Version)
            : WebhookErrors.EndpointNotFound;
    }
}

public sealed class CreateWebhookEndpointHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, ISecretEncryption encryption, IOptions<ServerOptions> server, BillingClock clock)
{
    public async Task<Result<CreatedWebhookEndpointResponse>> HandleAsync(CreateWebhookEndpointRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var eventTypes = request.EventTypes ?? [];
        if (!eventTypes.All(TahsilEventTypes.All.Contains))
        {
            return WebhookErrors.UnknownEventType;
        }

        var secret = WebhookSecrets.Generate();
        var created = WebhookEndpoint.Create(
            request.Url, request.Description, eventTypes, request.Enabled, server.Value.IsTestMode,
            id => encryption.Encrypt(secret, SecretPurposes.WebhookEndpointSecret, id), clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        var endpoint = created.Value;
        context.WebhookEndpoints.Add(endpoint);
        idempotency.RecordCreated(ResourceTypes.WebhookEndpoint, endpoint.Id);
        audit.Record(AuditActions.WebhookEndpointCreated, AuditSubject.Of(AuditSubjects.WebhookEndpoint, endpoint.Id), new Dictionary<string, object?> { ["url"] = endpoint.Url });
        await context.SaveChangesAsync(cancellationToken);
        return new CreatedWebhookEndpointResponse(WebhookMapper.ToResponse(endpoint), secret);
    }
}

public sealed class UpdateWebhookEndpointHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IOptions<ServerOptions> server, BillingClock clock)
{
    public async Task<Result<Versioned<WebhookEndpointResponse>>> HandleAsync(
        Guid id, UpdateWebhookEndpointRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.WebhookEndpoints.SingleOrDefaultAsync(endpoint => endpoint.Id == id, cancellationToken) is not { } endpoint)
        {
            return WebhookErrors.EndpointNotFound;
        }

        if (Concurrency.Check(endpoint, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        IReadOnlyList<string> eventTypes = request.EventTypes ?? endpoint.EventTypes;
        if (!eventTypes.All(TahsilEventTypes.All.Contains))
        {
            return WebhookErrors.UnknownEventType;
        }

        var updated = endpoint.Update(
            request.Url ?? endpoint.Url,
            request.HasDescription ? request.Description : endpoint.Description,
            eventTypes,
            request.Enabled ?? endpoint.Enabled,
            server.Value.IsTestMode,
            clock.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        audit.Record(AuditActions.WebhookEndpointUpdated, AuditSubject.Of(AuditSubjects.WebhookEndpoint, id), Changes.Of(
            ("url", request.Url is not null), ("description", request.HasDescription), ("eventTypes", request.EventTypes is not null), ("enabled", request.Enabled is not null)));
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<WebhookEndpointResponse>(WebhookMapper.ToResponse(endpoint), endpoint.Version);
    }
}

/// <summary>Deletes an endpoint with its delivery log; deliveries under way are dropped.</summary>
public sealed class DeleteWebhookEndpointHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit)
{
    public async Task<Result> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.WebhookEndpoints.SingleOrDefaultAsync(endpoint => endpoint.Id == id, cancellationToken) is not { } endpoint)
        {
            return WebhookErrors.EndpointNotFound;
        }

        context.WebhookEndpoints.Remove(endpoint);
        audit.Record(AuditActions.WebhookEndpointDeleted, AuditSubject.Of(AuditSubjects.WebhookEndpoint, id), new Dictionary<string, object?> { ["url"] = endpoint.Url });
        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Replaces an endpoint's secret; the previous one keeps signing beside it for the overlap (EVT-8).</summary>
public sealed class RotateWebhookSecretHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, ISecretEncryption encryption, IOptions<WebhookOptions> options, BillingClock clock)
{
    public async Task<Result<WebhookSecretResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.WebhookEndpoints.SingleOrDefaultAsync(endpoint => endpoint.Id == id, cancellationToken) is not { } endpoint)
        {
            return WebhookErrors.EndpointNotFound;
        }

        var secret = WebhookSecrets.Generate();
        var now = clock.GetUtcNow();
        var previousUntil = now + options.Value.SecretRotationOverlap;
        endpoint.RotateSecret(endpointId => encryption.Encrypt(secret, SecretPurposes.WebhookEndpointSecret, endpointId), previousUntil, now);
        audit.Record(AuditActions.WebhookEndpointSecretRotated, AuditSubject.Of(AuditSubjects.WebhookEndpoint, id), new Dictionary<string, object?> { ["previousSecretExpiresAt"] = previousUntil });
        await context.SaveChangesAsync(cancellationToken);
        return new WebhookSecretResponse(secret, previousUntil);
    }
}

/// <summary>Sends a <c>webhook.test</c> event to one endpoint, to try it out; it goes even to a disabled endpoint (EVT-14).</summary>
public sealed class SendTestWebhookHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IRequestContext request, IWorkerSignals signals, IOptions<ServerOptions> server, BillingClock clock)
{
    public async Task<Result<WebhookDeliveryResponse>> HandleAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!await context.WebhookEndpoints.AnyAsync(endpoint => endpoint.Id == endpointId, cancellationToken))
        {
            return WebhookErrors.EndpointNotFound;
        }

        var now = clock.GetUtcNow();
        var webhookEvent = new WebhookEvent(
            TahsilEventTypes.Test,
            now,
            WebhookEvents.ModeName(server.Value.Mode),
            new WebhookEventData(AuditSubjects.WebhookEndpoint, endpointId.ToString(), null, WebhookEvents.ActorOf(request.Caller.ToActor())));
        var delivery = WebhookDelivery.Create(endpointId, Guid.CreateVersion7(now), TahsilEventTypes.Test, WebhookEvents.Serialize(webhookEvent), now);
        context.WebhookDeliveries.Add(delivery);
        await context.SaveChangesAsync(cancellationToken);
        signals.Notify(WebhookFanOut.WorkerName);
        return WebhookMapper.ToResponse(delivery);
    }
}

public sealed record ListWebhookDeliveriesQuery(Guid? Endpoint = null, ContractStatus? Status = null, string? EventType = null, string? Cursor = null, int? Limit = null);

public sealed class ListWebhookDeliveriesHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<WebhookDelivery, Guid> Newest = new("newest", delivery => delivery.Id, delivery => delivery.Id, Descending: true);

    public async Task<Result<Page<WebhookDeliveryResponse>>> HandleAsync(ListWebhookDeliveriesQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var deliveries = context.WebhookDeliveries.AsNoTracking();
        if (query.Endpoint is { } endpoint)
        {
            deliveries = deliveries.Where(delivery => delivery.EndpointId == endpoint);
        }

        if (query.Status is { } status)
        {
            var domainStatus = status.Map<DomainStatus>();
            deliveries = deliveries.Where(delivery => delivery.Status == domainStatus);
        }

        if (query.EventType is { } eventType)
        {
            deliveries = deliveries.Where(delivery => delivery.EventType == eventType);
        }

        var page = await Newest.ReadAsync(deliveries, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<WebhookDeliveryResponse>([.. page.Items.Select(WebhookMapper.ToResponse)], page.NextCursor);
    }
}

public sealed class GetWebhookDeliveryHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<WebhookDeliveryResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.WebhookDeliveries.AsNoTracking().SingleOrDefaultAsync(delivery => delivery.Id == id, cancellationToken) is { } delivery
            ? WebhookMapper.ToResponse(delivery)
            : WebhookErrors.DeliveryNotFound;
    }
}

/// <summary>Sends an event again, with the same event ID, as a fresh series of attempts (EVT-14).</summary>
public sealed class RedeliverWebhookHandler(ITahsilDbContext context, IAuthorizer authorizer, IWorkerSignals signals, BillingClock clock)
{
    public async Task<Result<WebhookDeliveryResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.WebhooksWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.WebhookDeliveries.SingleOrDefaultAsync(delivery => delivery.Id == id, cancellationToken) is not { } delivery)
        {
            return WebhookErrors.DeliveryNotFound;
        }

        delivery.Redeliver(clock.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);
        signals.Notify(WebhookFanOut.WorkerName);
        return WebhookMapper.ToResponse(delivery);
    }
}

internal static class WebhookMapper
{
    public static WebhookEndpointResponse ToResponse(WebhookEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.Url,
        endpoint.Description,
        [.. endpoint.EventTypes],
        endpoint.Enabled,
        endpoint.FailingSince,
        endpoint.PreviousSecretExpiresAt,
        endpoint.CreatedAt,
        endpoint.UpdatedAt);

    public static WebhookDeliveryResponse ToResponse(WebhookDelivery delivery) => new(
        delivery.Id,
        delivery.EndpointId,
        delivery.EventId,
        delivery.EventType,
        delivery.Status.Map<ContractStatus>(),
        delivery.Attempts,
        delivery.CreatedAt,
        delivery.LastAttemptAt,
        delivery.Status == DomainStatus.Pending ? delivery.NextAttemptAt : null,
        delivery.ResponseStatusCode,
        delivery.ResponseBody,
        delivery.Error,
        JsonDocument.Parse(delivery.Payload).RootElement.Clone());
}
