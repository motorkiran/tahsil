using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Tahsil.Contracts;

public enum ConnectionEnvironment
{
    Sandbox,
    Production,
}

public enum ConnectionTestStatus
{
    Succeeded,
    Failed,
}

/// <summary>A credential or setting a provider's connections take.</summary>
/// <param name="Key">The key in <c>credentials</c> or <c>settings</c>.</param>
/// <param name="Label">A label for forms.</param>
/// <param name="Type"><c>text</c>, <c>secret</c>, <c>number</c>, <c>boolean</c>, <c>choice</c> or <c>url</c>.</param>
/// <param name="Required">Whether a connection must have it.</param>
/// <param name="Description">Where to find the value, or what it changes.</param>
/// <param name="Choices">The values of a <c>choice</c> field.</param>
/// <param name="Default">The value when none is given.</param>
public sealed record ProviderFieldResponse(string Key, string Label, string Type, bool Required, string? Description, IReadOnlyList<string>? Choices, string? Default);

/// <summary>What a provider can do; values are snake_case names.</summary>
public sealed record ProviderCapabilitiesResponse(
    IReadOnlyList<string> Entry,
    string SavesCards,
    string Setup,
    bool ChargesSavedCards,
    string Refunds,
    bool AsynchronousRefunds,
    string Voids,
    string FindByReference,
    string Notifications,
    bool IdempotentCharges);

/// <summary>A payment provider Tahsil has an adapter for.</summary>
public sealed record ProviderResponse(
    string Key,
    string Name,
    IReadOnlyList<ConnectionEnvironment> Environments,
    IReadOnlyList<string> Currencies,
    IReadOnlyDictionary<string, long> MinimumAmounts,
    IReadOnlyList<ProviderFieldResponse> Credentials,
    IReadOnlyList<ProviderFieldResponse> Settings,
    ProviderCapabilitiesResponse Capabilities);

/// <summary>Whether a credential is set; credentials are never returned (SEC-9).</summary>
/// <param name="Set">Whether the connection has a value.</param>
/// <param name="Last4">The last four characters of values long enough to keep them secret.</param>
public sealed record CredentialState(bool Set, string? Last4);

public sealed record ConnectionTestResponse(ConnectionTestStatus Status, string? Message, DateTimeOffset TestedAt);

public sealed record ConnectionResponse(
    Guid Id,
    string Key,
    string Provider,
    string Name,
    ConnectionEnvironment Environment,
    bool Enabled,
    IReadOnlyDictionary<string, CredentialState> Credentials,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<string> Currencies,
    Uri NotificationUrl,
    ConnectionTestResponse? LastTest,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateConnectionRequest
{
    [Required]
    public required string Key { get; init; }

    /// <summary>A provider key from <c>GET /providers</c>.</summary>
    [Required]
    public required string Provider { get; init; }

    [Required]
    public required string Name { get; init; }

    public required ConnectionEnvironment Environment { get; init; }

    /// <summary>The provider's credential fields. Stored encrypted and never returned.</summary>
    public IReadOnlyDictionary<string, string>? Credentials { get; init; }

    /// <summary>The provider's setting fields, as strings, numbers or booleans.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Settings { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Changes a connection. Credentials and settings merge: the fields given replace the current values, the others stay;
/// an empty credential or a <c>null</c> setting removes an optional field. The key, provider and environment never change.
/// </summary>
public sealed record UpdateConnectionRequest
{
    public string? Name { get; init; }

    public IReadOnlyDictionary<string, string>? Credentials { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Settings { get; init; }

    public bool? Enabled { get; init; }
}
