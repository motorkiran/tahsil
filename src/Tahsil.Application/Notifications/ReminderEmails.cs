using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Subscriptions;
using Tahsil.Domain.Customers;

namespace Tahsil.Application.Notifications;

/// <summary>
/// The trial ends soon (SUB-16): with a card, the amount and date of the first charge and how to cancel before it;
/// without one, how to keep the subscription.
/// </summary>
public sealed class TrialEndingComposer(ITahsilDbContext context, SubscriptionLines lines, SubscriptionPreviews previews, EmailFormat format, BillingClock clock)
    : ICustomerEmailComposer
{
    public string Type => EmailTypes.TrialEnding;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        if (subscription is not { Status: Domain.Subscriptions.SubscriptionStatus.Trialing, TrialEnded: false, TrialEnd: { } trialEnd })
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        var first = await previews.UpcomingAsync(subscription, customer, null, [], clock.GetUtcNow(), cancellationToken);
        return new ComposedEmail(customer, "trial-ending", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["subscription"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plan"] = await SubscriptionCanceledComposer.PlanAsync(lines, subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken),
                ["trial_end"] = format.Date(trialEnd, customer),
                ["with_card"] = message.Data?.GetValueOrDefault("mandatory") == "true",
                ["first_charge"] = first is null ? null : EmailFormat.Money(first.Total, subscription.Currency, customer),
            },
        });
    }
}

/// <summary>The subscription renews soon (NTF-6): when, and for how much.</summary>
public sealed class UpcomingRenewalComposer(ITahsilDbContext context, SubscriptionLines lines, SubscriptionPreviews previews, EmailFormat format, BillingClock clock)
    : ICustomerEmailComposer
{
    public string Type => EmailTypes.UpcomingRenewal;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        if (await previews.UpcomingAsync(subscription, customer, null, [], clock.GetUtcNow(), cancellationToken) is not { PeriodStart: { } renewsAt } renewal)
        {
            return null;
        }

        return new ComposedEmail(customer, "upcoming-renewal", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["subscription"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plan"] = await SubscriptionCanceledComposer.PlanAsync(lines, subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken),
                ["renews_at"] = format.Date(renewsAt, customer),
                ["amount"] = EmailFormat.Money(renewal.Total, subscription.Currency, customer),
            },
        });
    }
}

/// <summary>A card a subscription uses expires soon (CUS-15): which card, and which subscriptions it pays.</summary>
public sealed class CardExpiringComposer(ITahsilDbContext context, SubscriptionLines lines, Reminders reminders) : ICustomerEmailComposer
{
    public string Type => EmailTypes.CardExpiring;

    public async Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        var card = await context.PaymentMethods.AsNoTracking().SingleAsync(row => row.Id == message.SubjectId, cancellationToken);
        if (card.Status != PaymentMethodStatus.Active || await reminders.SubscriptionsUsingAsync(card, cancellationToken) is not { Count: > 0 } subscriptionIds)
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == card.CustomerId, cancellationToken);
        var plans = new List<string>();
        foreach (var subscription in await context.Subscriptions.AsNoTracking().Include(row => row.Items).Where(row => subscriptionIds.Contains(row.Id)).ToListAsync(cancellationToken))
        {
            plans.Add(await SubscriptionCanceledComposer.PlanAsync(lines, subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken));
        }

        return new ComposedEmail(customer, "card-expiring", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["card"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = $"{card.Brand} •••• {card.Last4}",
                ["expiry"] = card.ExpMonth is { } month && card.ExpYear is { } year ? $"{month:D2}/{year}" : null,
            },
            ["plans"] = plans,
        });
    }
}
