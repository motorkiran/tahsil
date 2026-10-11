using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Billing;

/// <summary>
/// Reminds customers of invoices sent to them and marks them overdue when their due date passes (INV-12, INV-13), and
/// sends the last reminder before dunning's final action for invoices collected automatically (DUN §7).
/// </summary>
internal sealed class InvoiceReminderWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<InvoiceReminderWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => "invoice-reminders";

    protected override bool CallsOtherServices => false;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.invoices
            WHERE status = 'open' AND next_reminder_at <= {now}
            ORDER BY next_reminder_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.invoices SET next_reminder_at = {until} WHERE id = {id}", cancellationToken);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<InvoiceReminders>().ProcessDueAsync(id, now, cancellationToken);
}
