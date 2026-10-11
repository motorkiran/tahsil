using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Billing;

/// <summary>Finalizes drafts that advance by themselves once their draft period ends (ENG-11).</summary>
internal sealed class InvoiceFinalizationWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<InvoiceFinalizationWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => "invoice-finalization";

    protected override bool CallsOtherServices => false;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.invoices
            WHERE status = 'draft' AND auto_advance AND finalize_at <= {now}
            ORDER BY finalize_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.invoices SET finalize_at = {until} WHERE id = {id}", cancellationToken);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<DraftFinalization>().FinalizeDueAsync(id, now, cancellationToken);
}
