namespace Tahsil.Domain.Webhooks;

public enum WebhookDeliveryStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>One event on its way to one endpoint, with the outcome of its latest attempt (EVT-14).</summary>
public sealed class WebhookDelivery
{
    public const int ResponseBodyMaxLength = 1000;
    public const int ErrorMaxLength = 1000;

    private WebhookDelivery()
    {
    }

    public Guid Id { get; private init; }

    public Guid EndpointId { get; private init; }

    /// <summary>The event's ID: its audit event's (EVT-4), the same in every attempt and for every endpoint, sent as <c>webhook-id</c>.</summary>
    public Guid EventId { get; private init; }

    public string EventType { get; private init; } = string.Empty;

    /// <summary>The event as JSON, the body of every attempt.</summary>
    public string Payload { get; private init; } = string.Empty;

    public WebhookDeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>When the next attempt is due; while an attempt is under way, when it may be taken over.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public int? ResponseStatusCode { get; private set; }

    public string? ResponseBody { get; private set; }

    public string? Error { get; private set; }

    public static WebhookDelivery Create(Guid endpointId, Guid eventId, string eventType, string payload, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        EndpointId = endpointId,
        EventId = eventId,
        EventType = eventType,
        Payload = payload,
        Status = WebhookDeliveryStatus.Pending,
        CreatedAt = now,
        NextAttemptAt = now,
    };

    /// <summary>Starts an attempt. Until <paramref name="leaseUntil"/>, no one else makes one.</summary>
    public void BeginAttempt(DateTimeOffset now, DateTimeOffset leaseUntil)
    {
        Attempts++;
        LastAttemptAt = now;
        NextAttemptAt = leaseUntil;
    }

    /// <summary>Hands back an attempt that was not made, such as when the instance stops, so that one is made now.</summary>
    public void AbandonAttempt(DateTimeOffset now)
    {
        Attempts--;
        NextAttemptAt = now;
    }

    public void Succeed(int statusCode, string? responseBody)
    {
        Status = WebhookDeliveryStatus.Succeeded;
        NextAttemptAt = null;
        Record(statusCode, responseBody, error: null);
    }

    /// <summary>Records a failed attempt; without <paramref name="retryAt"/>, the delivery has failed for good.</summary>
    public void Fail(int? statusCode, string? responseBody, string? error, DateTimeOffset? retryAt)
    {
        Status = retryAt is null ? WebhookDeliveryStatus.Failed : WebhookDeliveryStatus.Pending;
        NextAttemptAt = retryAt;
        Record(statusCode, responseBody, error);
    }

    /// <summary>Sends the event again with the same event ID, as a fresh series of attempts.</summary>
    public void Redeliver(DateTimeOffset now)
    {
        Status = WebhookDeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
    }

    private void Record(int? statusCode, string? responseBody, string? error)
    {
        ResponseStatusCode = statusCode;
        ResponseBody = Truncate(responseBody, ResponseBodyMaxLength);
        Error = Truncate(error, ErrorMaxLength);
    }

    private static string? Truncate(string? value, int maxLength) => value?.Length > maxLength ? value[..maxLength] : value;
}
