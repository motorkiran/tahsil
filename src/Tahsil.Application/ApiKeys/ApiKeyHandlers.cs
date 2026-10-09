using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.ApiKeys;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;

namespace Tahsil.Application.ApiKeys;

public sealed class CreateApiKeyHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IRequestContext request, IAuditLog audit, IOptions<ServerOptions> server, BillingClock clock)
{
    public async Task<Result<CreatedApiKeyResponse>> HandleAsync(CreateApiKeyRequest command, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ApiKeysWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (command.Permissions.FirstOrDefault(permission => !TahsilPermissions.All.Contains(permission)) is { } unknown)
        {
            return ApiKeyErrors.UnknownPermission(unknown);
        }

        // Nobody gives a key more than they may do themselves.
        if (command.Permissions.Any(permission => authorizer.Require(permission).IsFailure))
        {
            return ApiKeyErrors.PermissionNotHeld;
        }

        if (await context.ApiKeys.CountAsync(key => key.RevokedAt == null, cancellationToken) >= ApiKey.MaxActiveKeys)
        {
            return ApiKeyErrors.LimitReached;
        }

        var now = clock.GetUtcNow();
        var (secret, displayPrefix, hash) = ApiKeySecrets.Generate(server.Value.Mode);
        var created = ApiKey.Create(command.Name, command.Permissions, command.ExpiresAt, displayPrefix, hash, request.Caller.ToActor(), now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var key = created.Value;
        context.ApiKeys.Add(key);
        audit.Record(AuditActions.ApiKeyCreated, AuditSubject.Of(AuditSubjects.ApiKey, key.Id), new Dictionary<string, object?>
        {
            ["name"] = key.Name,
            ["permissions"] = key.Permissions,
            ["expiresAt"] = key.ExpiresAt,
        });
        await context.SaveChangesAsync(cancellationToken);

        return new CreatedApiKeyResponse(key.ToResponse(), secret);
    }
}

public sealed record ListApiKeysQuery(string? Cursor, int? Limit);

/// <summary>Lists API keys in creation order, revoked and expired ones included.</summary>
public sealed class ListApiKeysHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<ApiKey, Guid> ByCreation = new("created", key => key.Id, key => key.Id);

    public async Task<Result<Page<ApiKeyResponse>>> HandleAsync(ListApiKeysQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ApiKeysRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var limit = query.Limit ?? Paging.DefaultLimit;
        if (!Paging.IsValidLimit(limit))
        {
            return CommonErrors.InvalidLimit();
        }

        var page = await ByCreation.ReadAsync(context.ApiKeys.AsNoTracking(), query.Cursor, limit, cancellationToken);
        return page is null
            ? CommonErrors.InvalidCursor
            : new Page<ApiKeyResponse>([.. page.Items.Select(key => key.ToResponse())], page.NextCursor);
    }
}

public sealed class GetApiKeyHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<ApiKeyResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ApiKeysRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var key = await context.ApiKeys.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return key is null ? ApiKeyErrors.NotFound : key.ToResponse();
    }
}

/// <summary>Stops a key from working at once; it stays listed with the time it was revoked. Revoking again changes nothing.</summary>
public sealed class RevokeApiKeyHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, BillingClock clock)
{
    public async Task<Result<ApiKeyResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ApiKeysWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.ApiKeys.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken) is not { } key)
        {
            return ApiKeyErrors.NotFound;
        }

        if (key.RevokedAt is null)
        {
            key.Revoke(clock.GetUtcNow());
            audit.Record(AuditActions.ApiKeyRevoked, AuditSubject.Of(AuditSubjects.ApiKey, key.Id), new Dictionary<string, object?> { ["name"] = key.Name });
            await context.SaveChangesAsync(cancellationToken);
        }

        return key.ToResponse();
    }
}

internal static class ApiKeyMapping
{
    public static ApiKeyResponse ToResponse(this ApiKey key) => new(
        key.Id, key.Name, key.DisplayPrefix, key.Permissions, key.Actor.ToResponse(), key.CreatedAt, key.ExpiresAt, key.LastUsedAt, key.RevokedAt);
}
