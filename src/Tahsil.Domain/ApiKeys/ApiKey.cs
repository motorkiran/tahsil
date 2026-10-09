using Tahsil.Domain.Common;

namespace Tahsil.Domain.ApiKeys;

/// <summary>
/// A long-lived secret that an application calls the API with, holding a set of permissions. Only a hash of the
/// secret is kept; the secret is shown once, when the key is created.
/// </summary>
public sealed class ApiKey
{
    public const int NameMaxLength = 100;
    public const int DisplayPrefixLength = 16;

    /// <summary>Unrevoked keys an installation may have at once.</summary>
    public const int MaxActiveKeys = 50;

    // Used by EF Core.
    private ApiKey()
    {
    }

    public Guid Id { get; private init; }

    public string Name { get; private init; } = string.Empty;

    /// <summary>The start of the secret, which identifies the key in lists and logs without revealing it.</summary>
    public string DisplayPrefix { get; private init; } = string.Empty;

    /// <summary>The SHA-256 of the secret.</summary>
    public byte[] SecretHash { get; private init; } = [];

    public string[] Permissions { get; private init; } = [];

    public DateTimeOffset? ExpiresAt { get; private init; }

    public DateTimeOffset? LastUsedAt { get; private init; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Who created the key.</summary>
    public Actor Actor { get; private init; } = Actor.System;

    public DateTimeOffset CreatedAt { get; private init; }

    public static Result<ApiKey> Create(
        string name, IReadOnlyCollection<string> permissions, DateTimeOffset? expiresAt, string displayPrefix, byte[] secretHash, Actor createdBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return ApiKeyErrors.InvalidName;
        }

        if (permissions.Count == 0)
        {
            return ApiKeyErrors.NoPermissions;
        }

        if (expiresAt <= now)
        {
            return ApiKeyErrors.ExpiryInThePast;
        }

        return new ApiKey
        {
            Id = Guid.CreateVersion7(now),
            Name = name.Trim(),
            DisplayPrefix = displayPrefix,
            SecretHash = secretHash,
            Permissions = [.. permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ExpiresAt = expiresAt?.ToUniversalTime(),
            Actor = createdBy,
            CreatedAt = now,
        };
    }

    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is not { } expiry || now < expiry);

    /// <summary>Stops the key from working. Revoking a revoked key changes nothing.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public static class ApiKeyErrors
{
    public static readonly Error NotFound = Error.NotFound("api_key.not_found", "The API key does not exist.");

    public static readonly Error InvalidName = Error.Validation("api_key.invalid_name", "A name is required and is at most 100 characters.");

    public static readonly Error NoPermissions = Error.Validation("api_key.no_permissions", "A key needs at least one permission.");

    public static readonly Error ExpiryInThePast = Error.Validation("api_key.expiry_in_the_past", "The expiry date must be in the future.");

    public static readonly Error LimitReached = Error.Conflict(
        "api_key.limit_reached", $"The installation already has {ApiKey.MaxActiveKeys} API keys that are not revoked; revoke one first.");

    /// <summary>Nobody gives a key more than they may do themselves.</summary>
    public static readonly Error PermissionNotHeld = Error.Forbidden(
        "api_key.permission_not_held", "A key can only have permissions that its creator holds.");

    public static Error UnknownPermission(string permission) =>
        Error.Validation("api_key.unknown_permission", $"'{permission}' is not a Tahsil permission.");
}
