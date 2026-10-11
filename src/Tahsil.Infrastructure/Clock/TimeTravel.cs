using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tahsil.Application.Common;
using Tahsil.Application.TestClock;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;
using DomainClock = Tahsil.Domain.Clock.TestClock;

namespace Tahsil.Infrastructure.Clock;

/// <summary>
/// Time travel in Test mode (ENG-23): the billing clock moves to the earliest due instant up to the target, every
/// worker runs until nothing is due at that instant, outbox included, and so on until the target, so records get the
/// timestamps they would have had. One time travel runs at a time, across instances.
/// </summary>
internal sealed class TimeTravel(IServiceScopeFactory scopes, IServiceProvider services, TestClockOffset offset, TimeProvider system, BillingClock clock) : ITimeTravel
{
    /// <summary>A guard against a worker that keeps an item due without moving it on.</summary>
    private const int MaxSteps = 10_000;

    public async Task<TimeTravelSummary?> TravelAsync(DateTimeOffset target, Actor actor, CancellationToken cancellationToken)
    {
        // A session lock on a connection held for the whole travel; the steps themselves use other connections.
        await using var lockScope = scopes.CreateAsyncScope();
        var database = lockScope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database;
        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({AdvisoryLockKeys.TimeTravel}) AS \"Value\"").SingleAsync(cancellationToken))
            {
                return null;
            }

            try
            {
                return await StepAsync(target, actor, cancellationToken);
            }
            finally
            {
                await database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({AdvisoryLockKeys.TimeTravel})", CancellationToken.None);
            }
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

    private async Task<TimeTravelSummary> StepAsync(DateTimeOffset target, Actor actor, CancellationToken cancellationToken)
    {
        var from = clock.GetUtcNow();
        var steps = 0;
        await RunWorkersAsync(cancellationToken);
        while (steps < MaxSteps && await NextDueAsync(cancellationToken) is { } due && due <= target)
        {
            if (due > clock.GetUtcNow())
            {
                await SetClockAsync(due, actor, cancellationToken);
            }

            await RunWorkersAsync(cancellationToken);
            steps++;
        }

        await SetClockAsync(target, actor, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
        var payments = context.Payments.AsNoTracking().Where(payment => payment.CreatedAt >= from && payment.CreatedAt <= target);
        var summary = new TimeTravelSummary(
            from,
            target,
            steps,
            await context.Invoices.CountAsync(invoice => invoice.CreatedAt >= from && invoice.CreatedAt <= target && invoice.BillingReason != Domain.Invoices.BillingReason.Manual, cancellationToken),
            await payments.CountAsync(payment => payment.Initiator == Domain.Payments.PaymentInitiator.Merchant, cancellationToken),
            await payments.CountAsync(payment => payment.Initiator == Domain.Payments.PaymentInitiator.Merchant && payment.Status == Domain.Payments.PaymentStatus.Succeeded, cancellationToken),
            await payments.CountAsync(payment => payment.Initiator == Domain.Payments.PaymentInitiator.Merchant && payment.Status == Domain.Payments.PaymentStatus.Failed, cancellationToken),
            0);
        scope.ServiceProvider.GetRequiredService<IAuditLog>().Record(AuditActions.TestClockAdvanced, data: new Dictionary<string, object?>
        {
            ["from"] = from,
            ["to"] = target,
            ["steps"] = steps,
        }, actor: actor);
        await context.SaveChangesAsync(cancellationToken);
        return summary;
    }

    /// <summary>Sets the billing clock so that it reads at least <paramref name="instant"/>, on this instance at once.</summary>
    private async Task SetClockAsync(DateTimeOffset instant, Actor actor, CancellationToken cancellationToken)
    {
        var seconds = (long)Math.Ceiling((instant - system.GetUtcNow()).TotalSeconds);
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
        var stored = await context.TestClocks.SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            stored = DomainClock.Create(system.GetUtcNow());
            context.TestClocks.Add(stored);
        }

        if (seconds > stored.OffsetSeconds)
        {
            stored.AdvanceTo(seconds, actor, system.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken);
            offset.Set(seconds);
        }
    }

    /// <summary>Runs every worker until none finds anything due at the clock's current time.</summary>
    private async Task RunWorkersAsync(CancellationToken cancellationToken)
    {
        var workers = services.GetServices<IHostedService>().OfType<IRunnableWorker>().ToList();
        int processed;
        var rounds = 0;
        do
        {
            processed = 0;
            foreach (var worker in workers)
            {
                processed += await worker.RunBatchAsync(cancellationToken);
            }
        }
        while (processed > 0 && ++rounds < MaxSteps);
    }

    /// <summary>The earliest instant at which anything is due, across every due-time column.</summary>
    private async Task<DateTimeOffset?> NextDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var due = await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database.SqlQuery<DateTimeOffset?>($"""
            SELECT min(due) AS "Value" FROM (
                SELECT min(finalize_at) AS due FROM tahsil.invoices WHERE status = 'draft' AND auto_advance
                UNION ALL SELECT min(next_payment_attempt_at) FROM tahsil.invoices WHERE status = 'open' AND collection_method = 'charge_automatically'
                UNION ALL SELECT min(reconcile_at) FROM tahsil.payments WHERE status IN ('processing', 'requires_action')
                UNION ALL SELECT min(reconcile_at) FROM tahsil.refunds WHERE status = 'processing'
                UNION ALL SELECT min(next_attempt_at) FROM tahsil.provider_notifications WHERE status = 'pending'
                UNION ALL SELECT min(next_attempt_at) FROM tahsil.webhook_deliveries WHERE status = 'pending'
                UNION ALL SELECT min(next_attempt_at) FROM tahsil.outbox_messages WHERE status = 'pending'
                UNION ALL SELECT min(expires_at) FROM tahsil.payment_methods WHERE status = 'active'
                UNION ALL SELECT min(expires_at) FROM tahsil.checkout_sessions WHERE status = 'open'
            ) AS instants
            """).ToListAsync(cancellationToken);
        return due.SingleOrDefault();
    }
}
