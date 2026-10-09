namespace Tahsil.Infrastructure.Idempotency;

/// <summary>
/// One idempotent request of an API key: in progress under a lease, then its stored response for 24 hours. A create
/// handler records the resource it created on it in its own transaction (API-13).
/// </summary>
public sealed class IdempotencyRecord
{
    public const int KeyMaxLength = 255;
    public const int ResourceTypeMaxLength = 32;

    /// <summary>How long a request owns its key before a retry may take over.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    /// <summary>How long a response is kept for replays.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public Guid Id { get; init; }

    public Guid ApiKeyId { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    /// <summary>The SHA-256 of the method, path and raw body: a reused key must come with the same request.</summary>
    public byte[] RequestHash { get; init; } = [];

    public IdempotencyStatus Status { get; set; }

    public DateTimeOffset LeaseExpiresAt { get; set; }

    public string? ResourceType { get; set; }

    public Guid? ResourceId { get; set; }

    public int? ResponseStatus { get; set; }

    /// <summary>The headers a replay repeats (content type and location), as a JSON object.</summary>
    public string? ResponseHeaders { get; set; }

    public byte[]? ResponseBody { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

public enum IdempotencyStatus
{
    InProgress,
    Completed,
}
