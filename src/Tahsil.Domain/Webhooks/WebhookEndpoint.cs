using System.Net;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Webhooks;

/// <summary>
/// A URL of the application that receives events, signed with a secret of its own (EVT-7). It receives the event
/// types it subscribes to, or every type when it lists none. A rotated secret keeps signing beside the new one for an
/// overlap, so the application can switch without losing events (EVT-8).
/// </summary>
public sealed class WebhookEndpoint : AggregateRoot
{
    public const int UrlMaxLength = 2048;
    public const int DescriptionMaxLength = 256;

    private WebhookEndpoint()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>What provisioning documents know the endpoint by (PRV-2); endpoints made through the API may have none.</summary>
    public string? Key { get; private init; }

    public string Url { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public List<string> EventTypes { get; private set; } = [];

    /// <summary>The signing secret, encrypted for the endpoint.</summary>
    public byte[] Secret { get; private set; } = [];

    /// <summary>The secret before the last rotation, which still signs until <see cref="PreviousSecretExpiresAt"/>.</summary>
    public byte[]? PreviousSecret { get; private set; }

    public DateTimeOffset? PreviousSecretExpiresAt { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>When deliveries started failing without a success since; flags an endpoint that needs attention (EVT-11).</summary>
    public DateTimeOffset? FailingSince { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A new endpoint; the caller checks that the event types exist and the key is free.</summary>
    public static Result<WebhookEndpoint> Create(
        string? key, string url, string? description, IEnumerable<string> eventTypes, bool enabled, bool allowLoopbackHttp, Func<Guid, byte[]> encryptSecret, DateTimeOffset now)
    {
        if (key is not null && !Catalog.CatalogKey.IsValid(key))
        {
            return WebhookErrors.InvalidKey;
        }

        var endpoint = new WebhookEndpoint { Id = Guid.CreateVersion7(now), Key = key, CreatedAt = now, Enabled = enabled };
        var updated = endpoint.Update(url, description, eventTypes, enabled, allowLoopbackHttp, now);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        endpoint.Secret = encryptSecret(endpoint.Id);
        return endpoint;
    }

    public Result Update(string url, string? description, IEnumerable<string> eventTypes, bool enabled, bool allowLoopbackHttp, DateTimeOffset now)
    {
        if (!IsValidUrl(url, allowLoopbackHttp))
        {
            return WebhookErrors.InvalidUrl;
        }

        if (!Text.FitsOptional(description, DescriptionMaxLength))
        {
            return WebhookErrors.InvalidDescription;
        }

        Url = url;
        Description = Text.Normalize(description);
        EventTypes = [.. eventTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (Enabled != enabled)
        {
            Enabled = enabled;
            FailingSince = null;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Replaces the secret; the current one keeps signing until <paramref name="previousUntil"/>.</summary>
    public void RotateSecret(Func<Guid, byte[]> encryptSecret, DateTimeOffset previousUntil, DateTimeOffset now)
    {
        PreviousSecret = Secret;
        PreviousSecretExpiresAt = previousUntil;
        Secret = encryptSecret(Id);
        UpdatedAt = now;
    }

    /// <summary>The secrets that sign at <paramref name="now"/>: the current one, and the previous one during the overlap.</summary>
    public IReadOnlyList<byte[]> SigningSecrets(DateTimeOffset now) =>
        PreviousSecret is { } previous && PreviousSecretExpiresAt > now ? [Secret, previous] : [Secret];

    public bool Receives(string eventType) => Enabled && (EventTypes.Count == 0 || EventTypes.Contains(eventType));

    public void RecordSuccess() => FailingSince = null;

    public void RecordFailure(DateTimeOffset now) => FailingSince ??= now;

    /// <summary>
    /// An absolute HTTPS URL without credentials or a fragment (EVT-12). Plain HTTP is accepted for this machine only,
    /// in Test mode.
    /// </summary>
    public static bool IsValidUrl(string? url, bool allowLoopbackHttp) =>
        url is { Length: <= UrlMaxLength }
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && IsLocal(uri)));

    private static bool IsLocal(Uri uri) => uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
}
