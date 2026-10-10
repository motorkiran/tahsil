using Tahsil.Domain.Common;

namespace Tahsil.Domain.Webhooks;

public static class WebhookErrors
{
    public static readonly Error EndpointNotFound = Error.NotFound("webhook_endpoint.not_found", "The webhook endpoint does not exist.");

    public static readonly Error DeliveryNotFound = Error.NotFound("webhook_delivery.not_found", "The webhook delivery does not exist.");

    public static readonly Error InvalidUrl = Error.Validation(
        "webhook_endpoint.invalid_url", "The URL must be an absolute HTTPS URL without credentials or a fragment; plain HTTP is accepted for localhost in Test mode only.", "url");

    public static readonly Error InvalidDescription = Error.Validation("webhook_endpoint.invalid", "A description is at most 256 characters.", "description");

    public static readonly Error UnknownEventType = Error.Validation(
        "webhook_endpoint.unknown_event_type", "One or more event types do not exist; GET /webhooks/event-types lists them.", "eventTypes");
}
