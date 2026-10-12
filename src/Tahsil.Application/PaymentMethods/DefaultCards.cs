using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Domain.Auditing;

namespace Tahsil.Application.PaymentMethods;

/// <summary>
/// After a customer's default card changed in the portal: the invoices that waited for a card are tried with it once
/// the change commits (DUN-8), for the customer and for the subscriptions that took the card as their own.
/// </summary>
[OutboxMessage("payment_methods.retry_waiting_invoices")]
public sealed record RetryWaitingInvoices(Guid CustomerId, IReadOnlyList<Guid> SubscriptionIds);

/// <summary>
/// The portal's card update (CUS-13, PRT-11): the card becomes the customer's default, and the default of every
/// running subscription that used the previous one.
/// </summary>
public sealed class DefaultCards(ITahsilDbContext context, IAuditLog audit, IOutbox outbox)
{
    private static readonly string[] DefaultChanged = ["defaultPaymentMethod"];

    /// <summary>Runs in the caller's unit of work; <paramref name="cardId"/> is an active card of the customer.</summary>
    public async Task ReplaceAsync(Guid customerId, Guid cardId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var customer = await context.Customers.SingleAsync(row => row.Id == customerId, cancellationToken);
        var previous = customer.DefaultPaymentMethodId;
        var moved = new List<Guid>();
        if (previous is { } old && old != cardId)
        {
            foreach (var subscription in await context.Subscriptions.Where(row => row.CustomerId == customerId && row.DefaultPaymentMethodId == old).ToListAsync(cancellationToken))
            {
                if (subscription.SetDefaultPaymentMethod(cardId, now).IsSuccess)
                {
                    moved.Add(subscription.Id);
                    audit.Record(AuditActions.SubscriptionUpdated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
                    {
                        ["changed"] = DefaultChanged,
                        ["status"] = Subscriptions.SubscriptionMapper.Wire(subscription.Status),
                    }, customerId: customerId);
                }
            }
        }

        if (previous != cardId)
        {
            customer.SetDefaultPaymentMethod(cardId, now);
            audit.Record(AuditActions.CustomerUpdated, AuditSubject.Of(AuditSubjects.Customer, customerId), new Dictionary<string, object?> { ["changed"] = DefaultChanged }, customerId: customerId);
        }

        outbox.Enqueue(new RetryWaitingInvoices(customerId, moved));
    }
}

public sealed class WaitingInvoiceRetrier(Dunning dunning, BillingClock clock) : IOutboxMessageHandler<RetryWaitingInvoices>
{
    public async Task HandleAsync(RetryWaitingInvoices message, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await dunning.RetryWaitingAsync(message.CustomerId, null, now, cancellationToken);
        foreach (var subscriptionId in message.SubscriptionIds)
        {
            await dunning.RetryWaitingAsync(message.CustomerId, subscriptionId, now, cancellationToken);
        }
    }
}
