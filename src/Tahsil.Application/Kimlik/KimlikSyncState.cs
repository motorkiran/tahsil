using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Kimlik;

/// <summary>
/// How a subscription stands in Kimlik: whether Tahsil ever synced it, the subscription it mirrors to, the last
/// success, and the failure since, if any.
/// </summary>
public sealed record KimlikSyncState(bool IsSynced, Guid? KimlikSubscriptionId, DateTimeOffset? SyncedAt, string? Error, DateTimeOffset? FailingSince);

/// <summary>Reads a subscription's Kimlik sync state for the admin panel (ADM §3).</summary>
public sealed class GetKimlikSyncHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<KimlikSyncState>> HandleAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.SubscriptionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var mirror = await context.KimlikMirrors.AsNoTracking().SingleOrDefaultAsync(row => row.SubscriptionId == subscriptionId, cancellationToken);
        return new KimlikSyncState(mirror is not null, mirror?.KimlikSubscriptionId, mirror?.SyncedAt, mirror?.Error, mirror?.FailingSince);
    }
}
