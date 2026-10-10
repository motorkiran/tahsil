using Microsoft.Extensions.Logging;

namespace Tahsil.Providers.Abstractions;

/// <summary>
/// Everything an adapter may use for one call: the connection's environment, credentials and settings, an HTTP client,
/// the time, a logger and the URLs Tahsil exposes for the connection. Adapters never read configuration, the database or
/// other services (ADP-12).
/// </summary>
public sealed class ProviderContext
{
    public required Guid ConnectionId { get; init; }

    public required ProviderEnvironment Environment { get; init; }

    /// <summary>The decrypted credentials, by the keys of the descriptor's credential fields.</summary>
    public required IReadOnlyDictionary<string, string> Credentials { get; init; }

    /// <summary>The settings, by the keys of the descriptor's setting fields.</summary>
    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    /// <summary>The adapter's own client, from <c>IHttpClientFactory</c>, without retrying handlers (ADP-5).</summary>
    public required HttpClient Http { get; init; }

    public required TimeProvider Time { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>Where the provider sends notifications for this connection.</summary>
    public required Uri NotificationUrl { get; init; }

    public string Credential(string key) =>
        Credentials.TryGetValue(key, out var value) ? value : throw new InvalidOperationException($"The connection has no '{key}' credential.");

    public string? Setting(string key) => Settings.TryGetValue(key, out var value) ? value : null;
}

public sealed record ConnectionTestResult(bool Succeeded, string? FailureCode = null, string? Message = null)
{
    public static ConnectionTestResult Success { get; } = new(true);
}
