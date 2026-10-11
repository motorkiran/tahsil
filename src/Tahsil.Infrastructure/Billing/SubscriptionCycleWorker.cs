using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Subscriptions;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Billing;

/// <summary>Moves subscriptions through their periods when they are due (ENG-8): expiries, trial ends, cancellations and renewals.</summary>
internal sealed class SubscriptionCycleWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<SubscriptionCycleWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => "subscription-cycles";

    protected override bool CallsOtherServices => false;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.subscriptions
            WHERE next_cycle_at <= {now}
            ORDER BY next_cycle_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.subscriptions SET next_cycle_at = {until} WHERE id = {id}", cancellationToken);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<SubscriptionCycles>().ProcessAsync(id, now, cancellationToken);
}
