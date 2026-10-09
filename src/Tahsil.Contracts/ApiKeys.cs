using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Tahsil.Contracts;

/// <summary>An API key, without its secret. <c>DisplayPrefix</c> is the start of the secret, to recognize the key by.</summary>
public sealed record ApiKeyResponse(
    Guid Id,
    string Name,
    string DisplayPrefix,
    IReadOnlyList<string> Permissions,
    ActorResponse CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>A new API key with its secret <c>Key</c>, which is shown only this once. Callers send it as <c>Authorization: Bearer ths_…</c>.</summary>
public sealed record CreatedApiKeyResponse(ApiKeyResponse ApiKey, string Key);

public sealed record CreateApiKeyRequest
{
    [Required]
    [StringLength(100)]
    public required string Name { get; init; }

    /// <summary>The permissions the key may use; its creator must hold them.</summary>
    [Required]
    public required IReadOnlyList<string> Permissions { get; init; }

    /// <summary>When the key stops working; it does not expire when omitted.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// What API key secrets look like: <c>ths_live_</c> or <c>ths_test_</c>, by the mode of the installation, then 43
/// base64url characters, 256 random bits. The prefix lets secret scanners and people recognize them.
/// </summary>
public static class ApiKeyFormat
{
    public const string LivePrefix = "ths_live_";

    public const string TestPrefix = "ths_test_";

    public const int Length = 52;

    /// <summary>Whether the value could be a key at all, so that anything else is turned away without a lookup.</summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? value) =>
        value is { Length: Length }
        && (value.StartsWith(LivePrefix, StringComparison.Ordinal) || value.StartsWith(TestPrefix, StringComparison.Ordinal))
        && Base64Url.IsValid(value.AsSpan(LivePrefix.Length));

    /// <summary>Whether a well-formed key belongs to a Live mode installation.</summary>
    public static bool IsLive(string value) => value.StartsWith(LivePrefix, StringComparison.Ordinal);
}
