using Microsoft.EntityFrameworkCore;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Workers;

/// <summary>A queue of due items and the due time of its oldest; none when nothing waits in it.</summary>
internal sealed record QueueDue(string Queue, DateTimeOffset? Due);

/// <summary>
/// Every column that makes an item due for a worker, with the oldest due time in each: what time travel steps through,
/// and how far behind the workers are (ENG §11).
/// </summary>
internal static class DueQueues
{
    public static async Task<List<QueueDue>> OldestAsync(TahsilDbContext context, CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<QueueDue>($"""
            SELECT 'invoice-finalization' AS queue, min(finalize_at) AS due FROM tahsil.invoices WHERE status = 'draft' AND auto_advance
            UNION ALL SELECT 'subscription-cycles', min(next_cycle_at) FROM tahsil.subscriptions
            UNION ALL SELECT 'invoice-reminders', min(next_reminder_at) FROM tahsil.invoices WHERE status = 'open'
            UNION ALL SELECT 'dunning', min(dunning_deadline_at) FROM tahsil.invoices AS invoice
                WHERE status = 'open' AND dunning_ended_at IS NULL AND next_payment_attempt_at IS NULL
                    AND NOT EXISTS (SELECT 1 FROM tahsil.payments AS payment WHERE payment.invoice_id = invoice.id AND payment.status IN ('processing', 'requires_action'))
            UNION ALL SELECT 'payment-collection', min(next_payment_attempt_at) FROM tahsil.invoices WHERE status = 'open' AND collection_method = 'charge_automatically'
            UNION ALL SELECT 'payment-reconciliation', min(reconcile_at) FROM tahsil.payments WHERE status IN ('processing', 'requires_action')
            UNION ALL SELECT 'refund-reconciliation', min(reconcile_at) FROM tahsil.refunds WHERE status = 'processing'
            UNION ALL SELECT 'provider-notifications', min(next_attempt_at) FROM tahsil.provider_notifications WHERE status = 'pending'
            UNION ALL SELECT 'webhook-delivery', min(next_attempt_at) FROM tahsil.webhook_deliveries WHERE status = 'pending'
            UNION ALL SELECT 'outbox', min(next_attempt_at) FROM tahsil.outbox_messages WHERE status = 'pending'
            UNION ALL SELECT 'payment-method-expiry', min(expires_at) FROM tahsil.payment_methods WHERE status = 'active'
            UNION ALL SELECT 'checkout-expiry', min(expires_at) FROM tahsil.checkout_sessions WHERE status = 'open'
            """).ToListAsync(cancellationToken);
}
