using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Application.Invoices;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Subscriptions;

/// <summary>Removes a subscription's discount (CPN-14): invoices created afterwards are not discounted, and drafts drop it when finalized.</summary>
public sealed class RemoveSubscriptionDiscountHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IBillingLocks locks, Redemptions redemptions, SubscriptionMapper mapper)
{
    private static readonly string[] Changed = ["discount"];

    public async Task<Result<Versioned<SubscriptionResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.SubscriptionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        Subscription subscription;
        await using (var transaction = await context.BeginTransactionAsync(cancellationToken))
        {
            await locks.LockSubscriptionAsync(id, cancellationToken);
            if (await context.Subscriptions.Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == id, cancellationToken) is not { } found)
            {
                return SubscriptionErrors.NotFound;
            }

            subscription = found;
            if (subscription.DiscountId is not null)
            {
                await redemptions.RemoveFromSubscriptionAsync(subscription, cancellationToken);
                audit.Record(AuditActions.SubscriptionUpdated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
                {
                    ["changed"] = Changed,
                    ["status"] = SubscriptionMapper.Wire(subscription.Status),
                }, customerId: subscription.CustomerId);
                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return new Versioned<SubscriptionResponse>(await mapper.ToResponseAsync(subscription, cancellationToken), subscription.Version);
    }
}
