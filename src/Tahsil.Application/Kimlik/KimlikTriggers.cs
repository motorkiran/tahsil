using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Kimlik;

/// <summary>What queues a sync (KML-16): the audit events of a subscription's status, period, trial, cancellation schedule and items.</summary>
public static class KimlikTriggers
{
    public static SyncKimlikSubscription? From(string action, AuditSubject? subject) =>
        subject is { Type: AuditSubjects.Subscription } && Guid.TryParse(subject.Id, out var id)
        && action is AuditActions.SubscriptionCreated or AuditActions.SubscriptionUpdated or AuditActions.SubscriptionRenewed
            or AuditActions.SubscriptionCanceled or AuditActions.SubscriptionPastDue
            ? new SyncKimlikSubscription(id)
            : null;

    /// <summary>
    /// Queues a sync of the subscriptions that may be mirrored and that a change outside them concerns: those not
    /// over, and those with a Kimlik subscription.
    /// </summary>
    public static async Task QueueAsync(ITahsilDbContext context, IOutbox outbox, IQueryable<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        var ids = await subscriptions
            .Where(subscription => (subscription.Status != SubscriptionStatus.Canceled && subscription.Status != SubscriptionStatus.IncompleteExpired)
                || context.KimlikMirrors.Any(mirror => mirror.SubscriptionId == subscription.Id && mirror.KimlikSubscriptionId != null))
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            outbox.Enqueue(new SyncKimlikSubscription(id));
        }
    }
}

/// <summary>
/// SUB-2: with the connector on, a customer linked to Kimlik has at most one subscription that is not over with a
/// product that has a Kimlik plan, and it has one such product, since Kimlik keeps one subscription per subscriber.
/// </summary>
public sealed class KimlikPlanRule(ITahsilDbContext context, IOptions<KimlikOptions> options)
{
    public static readonly Error Conflict = Error.Conflict(
        "subscription.kimlik_plan_conflict", "The customer may have one subscription with a Kimlik plan, with one such product; change that subscription's plan instead.");

    public async Task<Error?> CheckAsync(
        Guid? customerId, KimlikSubscriber? subscriber, IReadOnlyList<SubscriptionItemSpec> items, Guid? subscriptionId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || subscriber is null)
        {
            return null;
        }

        var planned = items.Where(item => item.Product.KimlikPlan is not null).Select(item => item.Product.Id).Distinct().Count();
        if (planned == 0)
        {
            return null;
        }

        return planned > 1 || (customerId is { } customer && await context.Subscriptions.AnyAsync(
                subscription => subscription.CustomerId == customer && subscription.Id != subscriptionId
                    && subscription.Status != SubscriptionStatus.Canceled && subscription.Status != SubscriptionStatus.IncompleteExpired
                    && subscription.Items.Any(item => context.Prices.Any(price => price.Id == item.PriceId
                        && context.Products.Any(product => product.Id == price.ProductId && product.KimlikPlan != null))),
                cancellationToken))
            ? Conflict
            : null;
    }
}
