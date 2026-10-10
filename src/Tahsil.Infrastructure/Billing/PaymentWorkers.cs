using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Billing;

/// <summary>
/// Charges open invoices collected automatically when their next attempt is due (ENG-13). The provider is called
/// outside any transaction: the claim leases the invoice, and the payment written ahead keeps the call reconcilable.
/// </summary>
internal sealed class PaymentCollectionWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<PaymentCollectionWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => WorkerNames.PaymentCollection;

    protected override bool CallsOtherServices => true;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.invoices
            WHERE status = 'open' AND collection_method = 'charge_automatically' AND next_payment_attempt_at <= {now}
            ORDER BY next_payment_attempt_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.invoices SET next_payment_attempt_at = {until} WHERE id = {id}", cancellationToken);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<MerchantPayments>().CollectDueAsync(id, cancellationToken);
}

/// <summary>Asks providers about payments whose outcome is not known yet, when their reconciliation is due (ENG-15).</summary>
internal sealed class ReconciliationWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<ReconciliationWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => WorkerNames.Reconciliation;

    protected override bool CallsOtherServices => true;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.payments
            WHERE status IN ('processing', 'requires_action') AND reconcile_at <= {now}
            ORDER BY reconcile_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.payments SET reconcile_at = {until} WHERE id = {id}", cancellationToken);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<PaymentReconciliation>().ReconcileAsync(id, cancellationToken);
}

/// <summary>Asks providers about refunds still processing, when their reconciliation is due (PAY-19).</summary>
internal sealed class RefundReconciliationWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, WorkerSignals signals, ILogger<RefundReconciliationWorker> logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    public override string Name => WorkerNames.RefundReconciliation;

    protected override bool CallsOtherServices => true;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.refunds
            WHERE status = 'processing' AND reconcile_at <= {now}
            ORDER BY reconcile_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.refunds SET reconcile_at = {until} WHERE id = {id}", cancellationToken);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<RefundReconciliation>().ReconcileAsync(id, cancellationToken);
}
