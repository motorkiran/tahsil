using System.Globalization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Notifications;
using Tahsil.Application.Payments;
using Tahsil.Application.Subscriptions;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Invoices;

namespace Tahsil.Application.Invoices;

/// <summary>
/// Invoices sent to customers (INV-12, INV-13, DUN-11): the email when issued, reminders at offsets from the start of
/// the due day, and the due date passing, which makes the subscription past due and starts dunning, whose final action
/// comes after the last reminder.
/// </summary>
public sealed class InvoiceReminders(
    ITahsilDbContext context, IAuditLog audit, IOutbox outbox, SubscriptionSettlement subscriptions, IOptions<InvoiceOptions> invoices, IOptions<DunningOptions> dunning)
{
    /// <summary>A finalized invoice to be paid by the customer: emailed, and its first reminder scheduled (INV-12).</summary>
    public void Issued(Invoice invoice, DateTimeOffset now)
    {
        CustomerEmails.Enqueue(outbox, EmailTypes.InvoiceIssued, invoice.Id, invoice.Id.ToString());
        invoice.ScheduleReminder(Next(invoice, now), now);
    }

    /// <summary>
    /// The reminders and the due date passing that are due, in the reminder worker's transaction, which holds the
    /// invoice's lock; for an invoice collected automatically, the last reminder before dunning's final action (DUN §7).
    /// </summary>
    public async Task ProcessDueAsync(Guid invoiceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is { Status: InvoiceStatus.Open, CollectionMethod: CollectionMethod.ChargeAutomatically } charged)
        {
            if (charged is { DunningStartedAt: not null, DunningEndedAt: null })
            {
                CustomerEmails.Enqueue(outbox, EmailTypes.DunningFinalReminder, charged.Id, charged.Id.ToString());
            }

            charged.ScheduleReminder(null, now);
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        if (await context.FindInvoiceAsync(invoiceId, cancellationToken) is not
            { Status: InvoiceStatus.Open, CollectionMethod: CollectionMethod.SendInvoice, DueDate: { } due, NextReminderAt: { } from } invoice)
        {
            return;
        }

        foreach (var (offset, _) in Schedule(due).Where(reminder => reminder.At >= from && reminder.At <= now))
        {
            CustomerEmails.Enqueue(outbox, EmailTypes.InvoiceReminder, invoice.Id, $"{invoice.Id}:{(long)offset.TotalSeconds}", new Dictionary<string, string>
            {
                ["offsetDays"] = offset.TotalDays.ToString(CultureInfo.InvariantCulture),
            });
        }

        if (invoice.DunningStartedAt is null && OverdueAt(due) <= now)
        {
            // The final action comes a while after the last reminder, which tells the customer what is coming (DUN-11).
            invoice.MarkOverdue(Schedule(due).Select(reminder => reminder.At).Append(OverdueAt(due)).Max() + dunning.Value.FinalReminderBefore, now);
            audit.Record(AuditActions.InvoiceOverdue, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), new Dictionary<string, object?>
            {
                ["number"] = invoice.Number,
                ["dueDate"] = invoice.DueDate,
                ["amountRemaining"] = invoice.AmountRemaining,
            }, customerId: invoice.CustomerId);
            await subscriptions.CustomerFailureAsync(invoice, failureCode: null, now, cancellationToken);
        }

        invoice.ScheduleReminder(Next(invoice, now), now);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The due date passes at the end of the due day.</summary>
    private static DateTimeOffset OverdueAt(DateTimeOffset dueDate) => dueDate.AddTicks(TimeSpan.TicksPerMicrosecond);

    /// <summary>The reminders' offsets and instants, from the start of the due day, which ends at the due date.</summary>
    private IEnumerable<(TimeSpan Offset, DateTimeOffset At)> Schedule(DateTimeOffset dueDate)
    {
        var dayStart = OverdueAt(dueDate).AddDays(-1);
        return invoices.Value.ReminderOffsets.Select(offset => (offset, dayStart + offset));
    }

    /// <summary>The next reminder after <paramref name="after"/>, or the due date passing if it has not yet.</summary>
    private DateTimeOffset? Next(Invoice invoice, DateTimeOffset after)
    {
        if (invoice.DueDate is not { } due)
        {
            return null;
        }

        var instants = Schedule(due).Select(reminder => reminder.At);
        if (invoice.DunningStartedAt is null)
        {
            instants = instants.Append(OverdueAt(due));
        }

        return instants.Where(at => at > after).Order().Cast<DateTimeOffset?>().FirstOrDefault();
    }
}
