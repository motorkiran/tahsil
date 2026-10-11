using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Billing;

/// <summary>Runs dunning's final action for invoices whose schedule ran out unpaid (DUN-6).</summary>
internal sealed class DunningWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<DunningWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => "dunning";

    protected override bool CallsOtherServices => false;

    // A retry still scheduled, or a payment in flight, goes first; the final action waits for its outcome.
    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.invoices AS invoice
            WHERE status = 'open' AND dunning_ended_at IS NULL AND dunning_deadline_at <= {now} AND next_payment_attempt_at IS NULL
                AND NOT EXISTS (SELECT 1 FROM tahsil.payments AS payment
                    WHERE payment.invoice_id = invoice.id AND payment.status IN ('processing', 'requires_action'))
            ORDER BY dunning_deadline_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.invoices SET dunning_deadline_at = {until} WHERE id = {id}", cancellationToken);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<Dunning>().FinalActionAsync(id, now, cancellationToken);
}
