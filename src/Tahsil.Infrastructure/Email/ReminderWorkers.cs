using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Notifications;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Email;

/// <summary>
/// The reminder windows of ENG-20 as SQL: each opens the notice ahead of its target and closes at the target, and the
/// mark on the subject keeps a reminder to once per occurrence. The notices are whole days from validated settings and
/// are written into the SQL; <c>{0}</c> is the time.
/// </summary>
internal static class ReminderWindows
{
    public static string TrialEnding(NotificationOptions options) => $$"""
        FROM tahsil.subscriptions AS subject
        WHERE status = 'trialing' AND current_period_start < trial_end AND trial_reminded_for IS DISTINCT FROM trial_end
            AND trial_end - {{Days(options.TrialEndingNoticeDays)}} <= {0} AND {0} < trial_end
        """;

    public static string UpcomingRenewal(NotificationOptions options) => $$"""
        FROM tahsil.subscriptions AS subject
        WHERE status IN ('active', 'past_due') AND (cancel_at IS NULL OR cancel_at > current_period_end)
            AND renewal_reminded_for IS DISTINCT FROM current_period_end AND {{RenewalNotice(options)}} > interval '0'
            AND current_period_end - {{RenewalNotice(options)}} <= {0} AND {0} < current_period_end
        """;

    public static string CardExpiring(NotificationOptions options) => $$"""
        FROM tahsil.payment_methods AS subject
        WHERE status = 'active' AND expiry_reminded_at IS NULL
            AND expires_at - {{Days(options.CardExpiringNoticeDays)}} <= {0} AND {0} < expires_at
        """;

    /// <summary>When the next window opens that is not closed by then, for time travel to step to.</summary>
    public static string NextOpening(NotificationOptions options) => $$"""
        SELECT min(due) AS "Value" FROM (
            SELECT min(trial_end - {{Days(options.TrialEndingNoticeDays)}}) AS due FROM tahsil.subscriptions
                WHERE status = 'trialing' AND current_period_start < trial_end AND trial_reminded_for IS DISTINCT FROM trial_end AND trial_end > {0}
            UNION ALL SELECT min(current_period_end - {{RenewalNotice(options)}}) FROM tahsil.subscriptions
                WHERE status IN ('active', 'past_due') AND (cancel_at IS NULL OR cancel_at > current_period_end)
                    AND renewal_reminded_for IS DISTINCT FROM current_period_end AND {{RenewalNotice(options)}} > interval '0' AND current_period_end > {0}
            UNION ALL SELECT min(expires_at - {{Days(options.CardExpiringNoticeDays)}}) FROM tahsil.payment_methods
                WHERE status = 'active' AND expiry_reminded_at IS NULL AND expires_at > {0}
        ) AS openings
        """;

    private static string Days(int days) => $"interval '{days.ToString(CultureInfo.InvariantCulture)} days'";

    /// <summary>The renewal notice by the subscription's interval, which is stored by its snake-case name.</summary>
    private static string RenewalNotice(NotificationOptions options) =>
        $"(CASE interval WHEN 'year' THEN {Days(options.RenewalNoticeDays("year"))} WHEN 'month' THEN {Days(options.RenewalNoticeDays("month"))} "
        + $"WHEN 'week' THEN {Days(options.RenewalNoticeDays("week"))} ELSE {Days(options.RenewalNoticeDays("day"))} END)";
}

/// <summary>Takes the next subject whose reminder window is open, skipping one that failed in the last minute.</summary>
internal abstract class ReminderWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, IOptions<NotificationOptions> notifications, WorkerSignals signals, ILogger logger)
    : DueWorker(scopes, clock, options, signals, logger)
{
    protected override bool CallsOtherServices => false;

    protected NotificationOptions Notifications => notifications.Value;

    /// <summary>The window's <c>FROM</c> and <c>WHERE</c>, over the table aliased <c>subject</c>.</summary>
    protected abstract string Window { get; }

#pragma warning disable EF1002 // The window holds no input: only validated whole numbers from configuration.
    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQueryRaw<Guid>(
            $$"""
            SELECT subject.id AS "Value" {{Window}}
                AND NOT EXISTS (SELECT 1 FROM tahsil.worker_failures AS failure
                    WHERE failure.worker = {1} AND failure.item_id = subject.id AND failure.last_failed_at > {2})
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """,
            now,
            Name,
            now.AddMinutes(-1)).ToListAsync(cancellationToken)) is [var id] ? id : null;
#pragma warning restore EF1002

    // The window stays open until the reminder is marked; a subject that just failed waits a minute (see LockNextDueAsync).
    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class TrialEndingReminderWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, IOptions<NotificationOptions> notifications, WorkerSignals signals, ILogger<TrialEndingReminderWorker> logger)
    : ReminderWorker(scopes, clock, options, notifications, signals, logger)
{
    public override string Name => "trial-ending-reminders";

    protected override string Window => ReminderWindows.TrialEnding(Notifications);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<Reminders>().TrialEndingAsync(id, cancellationToken);
}

internal sealed class RenewalReminderWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, IOptions<NotificationOptions> notifications, WorkerSignals signals, ILogger<RenewalReminderWorker> logger)
    : ReminderWorker(scopes, clock, options, notifications, signals, logger)
{
    public override string Name => "renewal-reminders";

    protected override string Window => ReminderWindows.UpcomingRenewal(Notifications);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<Reminders>().UpcomingRenewalAsync(id, cancellationToken);
}

internal sealed class CardExpiryReminderWorker(
    IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, IOptions<NotificationOptions> notifications, WorkerSignals signals, ILogger<CardExpiryReminderWorker> logger)
    : ReminderWorker(scopes, clock, options, notifications, signals, logger)
{
    public override string Name => "card-expiry-reminders";

    protected override string Window => ReminderWindows.CardExpiring(Notifications);

    protected override Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        services.GetRequiredService<Reminders>().CardExpiringAsync(id, cancellationToken);
}
