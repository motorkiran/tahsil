using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Kimlik;
using Tahsil.Domain.Subscriptions;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Kimlik;

/// <summary>
/// Every <c>ReconcileInterval</c>, queues a sync of every mirrored subscription that is not over, of every one that
/// ended in the last seven days, and of every one with a Kimlik subscription, catching up what was missed while Kimlik
/// was unavailable (KML-19).
/// </summary>
internal sealed class KimlikReconciliation(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> workers,
    IOptions<KimlikOptions> options,
    ILogger<KimlikReconciliation> logger) : PeriodicJob(scopes, clock, workers, logger)
{
    private const int BatchSize = 500;

    private readonly BillingClock _clock = clock;

    public override string Name => "kimlik-reconciliation";

    protected override long LockKey => AdvisoryLockKeys.KimlikReconciliation;

    protected override TimeSpan Interval => options.Value.ReconcileInterval;

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var context = services.GetRequiredService<TahsilDbContext>();
        var outbox = services.GetRequiredService<IOutbox>();
        var recently = _clock.GetUtcNow().AddDays(-7);
        var ids = await context.Subscriptions.AsNoTracking()
            .Where(subscription =>
                context.KimlikMirrors.Any(mirror => mirror.SubscriptionId == subscription.Id && mirror.KimlikSubscriptionId != null)
                || ((subscription.Status != SubscriptionStatus.Canceled && subscription.Status != SubscriptionStatus.IncompleteExpired || subscription.EndedAt >= recently)
                    && context.Customers.Any(customer => customer.Id == subscription.CustomerId && customer.KimlikSubscriberId != null)
                    && subscription.Items.Any(item => context.Prices.Any(price => price.Id == item.PriceId
                        && context.Products.Any(product => product.Id == price.ProductId && product.KimlikPlan != null)))))
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        foreach (var batch in ids.Chunk(BatchSize))
        {
            foreach (var id in batch)
            {
                outbox.Enqueue(new SyncKimlikSubscription(id));
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
