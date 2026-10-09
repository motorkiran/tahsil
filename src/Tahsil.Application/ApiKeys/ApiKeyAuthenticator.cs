using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Application.ApiKeys;

/// <summary>
/// Turns the key of a request into its caller. Unknown, revoked and expired keys get one answer, so a caller learns
/// nothing about which; a key from the other mode is told so, because that mistake is easy to make and to fix.
/// </summary>
public sealed class ApiKeyAuthenticator(ITahsilDbContext context, IOptions<ServerOptions> server, TimeProvider time)
{
    /// <summary>How often the time a key was last used is written, at most.</summary>
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    public async Task<Result<ApiKeyCaller>> AuthenticateAsync(string? key, CancellationToken cancellationToken)
    {
        if (!ApiKeyFormat.IsWellFormed(key))
        {
            return AuthErrors.Unauthorized;
        }

        if (ApiKeyFormat.IsLive(key) != (server.Value.Mode == TahsilMode.Live))
        {
            return AuthErrors.WrongMode(server.Value.Mode);
        }

        // Keys expire in real time: the billing clock's time travel does not age them.
        var now = time.GetUtcNow();
        var hash = SecretHashing.Sha256(key);
        var apiKey = await context.ApiKeys.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.SecretHash == hash, cancellationToken);
        if (apiKey is null || !apiKey.IsActiveAt(now))
        {
            return AuthErrors.Unauthorized;
        }

        if (apiKey.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUsedPrecision)
        {
            await context.ApiKeys.Where(candidate => candidate.Id == apiKey.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.LastUsedAt, now), cancellationToken);
        }

        return new ApiKeyCaller(apiKey.Id, apiKey.Permissions.ToHashSet(StringComparer.Ordinal));
    }
}
