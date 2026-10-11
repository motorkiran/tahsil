using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Billing;

/// <summary>Expires open checkout sessions once their time runs out (DOM-10).</summary>
internal sealed class CheckoutExpiryWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<CheckoutExpiryWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => "checkout-expiry";

    // Expiring reconciles a payment in flight with its provider first (ENG-18).
    protected override bool CallsOtherServices => true;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.checkout_sessions AS session
            WHERE status = 'open' AND expires_at <= {now}
                AND NOT EXISTS (SELECT 1 FROM tahsil.worker_failures AS failure
                    WHERE failure.worker = {Name} AND failure.item_id = session.id AND failure.last_failed_at > {now.AddMinutes(-1)})
            ORDER BY expires_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    // An open session stays due until it is expired; a session that just failed waits a minute (see LockNextDueAsync).
    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        await services.GetRequiredService<CheckoutExpiry>().ExpireAsync(id, cancellationToken);
}
