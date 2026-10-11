using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.PaymentMethods;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Billing;

/// <summary>Marks active cards expired once their expiry passes (CUS-14).</summary>
internal sealed class PaymentMethodExpiryWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<PaymentMethodExpiryWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => "payment-method-expiry";

    protected override bool CallsOtherServices => false;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.payment_methods AS method
            WHERE status = 'active' AND expires_at <= {now}
                AND NOT EXISTS (SELECT 1 FROM tahsil.worker_failures AS failure
                    WHERE failure.worker = {Name} AND failure.item_id = method.id AND failure.last_failed_at > {now.AddMinutes(-1)})
            ORDER BY expires_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    // An expired card stays due until it is marked; a card that just failed waits a minute (see LockNextDueAsync).
    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<PaymentMethodExpiry>().ExpireAsync(id, cancellationToken);
}
