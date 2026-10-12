using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Catalog;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Subscriptions;
using Tahsil.Contracts;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;
using Tahsil.Domain.Portal;
using Tahsil.Domain.Subscriptions;
using Model = Tahsil.Domain.Catalog;
using SubscriptionStatus = Tahsil.Domain.Subscriptions.SubscriptionStatus;

namespace Tahsil.Application.Portal;

/// <summary>A price a customer may switch to (PRT-5): its name, how often it bills, and one unit's amount per period with tax.</summary>
public sealed record PlanOffer(Guid PriceId, string Name, string Recurrence, long Amount, bool IsCurrent);

/// <summary>What a customer may change a subscription to: the prices on offer and the quantities allowed.</summary>
public sealed record PlanChoices(Subscription Subscription, int Quantity, IReadOnlyList<PlanOffer> Offers, int MinimumQuantity, int MaximumQuantity)
{
    public bool OffersQuantity => MaximumQuantity > MinimumQuantity;
}

/// <summary>
/// A plan change before it is confirmed (PRT-5 to PRT-7): when it takes effect, what is charged or credited now, the
/// next renewal, and the button that names the obligation.
/// </summary>
public sealed record PlanChangeQuote(
    PlanOffer Offer, int Quantity, bool Upgrade, bool Now, DateTimeOffset EffectiveAt, long DueNow, long Credit, UpcomingRenewalResponse? NextRenewal, string Currency, string ButtonText);

/// <summary>A confirmed plan change; <paramref name="PayUrl"/> is the invoice's page when the card could not pay it (PRT-6).</summary>
public sealed record PlanChanged(Uri? PayUrl);

public static class PortalPlanErrors
{
    public static readonly Error NotOffered = Error.Validation("portal.plan_not_offered", "This plan or quantity is not offered.");

    public static readonly Error NothingChanges = Error.Validation("portal.nothing_changes", "This is your current plan.");
}

/// <summary>
/// Plan changes in the portal (PRT §3): among the prices of <c>Tahsil:Portal:Products</c> and within
/// <c>QuantityLimits</c>, an upgrade now and charged, a downgrade at the period end unless configured otherwise. The
/// change itself is the API's (<see cref="UpdateSubscriptionHandler"/>), made by the customer for a subscription of
/// their own, and recorded with the amounts they were shown.
/// </summary>
public sealed class PortalPlans(
    ITahsilDbContext context,
    IPageCaller caller,
    SubscriptionLines lines,
    SubscriptionChanges changes,
    PreviewSubscriptionChangeHandler previews,
    UpdateSubscriptionHandler updates,
    IOptions<PortalOptions> portal,
    IOptions<BillingOptions> billing,
    BillingClock clock)
{
    /// <summary>
    /// The choices for a subscription of the session's customer: active or trialing, not ending, with one licensed
    /// item. None when the feature is off or the subscription is not theirs to change.
    /// </summary>
    public async Task<PlanChoices?> ChoicesAsync(PortalSession session, Guid subscriptionId, string locale, CancellationToken cancellationToken)
    {
        caller.Use(new CustomerCaller(session.CustomerId, CustomerSessionKind.Portal, session.Id));
        var options = portal.Value;
        if (!options.IsEnabled(PortalFeatures.PlanChange)
            || await context.Subscriptions.AsNoTracking().Include(row => row.Items)
                .SingleOrDefaultAsync(row => row.Id == subscriptionId && row.CustomerId == session.CustomerId, cancellationToken)
                is not { Status: SubscriptionStatus.Active or SubscriptionStatus.Trialing, CancelAt: null, Items: [{ Quantity: { } quantity } item] } subscription)
        {
            return null;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == session.CustomerId, cancellationToken);
        var prices = new List<Model.Price> { await context.Prices.AsNoTracking().SingleAsync(price => price.Id == item.PriceId, cancellationToken) };
        foreach (var offered in options.Products)
        {
            if (await context.Products.AsNoTracking().SingleOrDefaultAsync(product => product.Key == offered.Product && !product.IsArchived, cancellationToken) is not { } product)
            {
                continue;
            }

            var ofProduct = await context.Prices.AsNoTracking()
                .Where(price => price.ProductId == product.Id && !price.IsArchived && price.Type == Model.PriceType.Recurring
                    && price.UsageType != Model.UsageType.Metered && price.Currency == subscription.Currency)
                .OrderBy(price => price.CreatedAt).ThenBy(price => price.Id)
                .ToListAsync(cancellationToken);
            prices.AddRange(ofProduct.Where(price => offered.Prices.Count == 0 || (price.Key is { } key && offered.Prices.Contains(key))));
        }

        var now = clock.GetUtcNow();
        var offers = new List<PlanOffer>();
        foreach (var price in prices.DistinctBy(price => price.Id))
        {
            var spec = (await lines.PricedAsync([(price.Id, 1)], cancellationToken))[0];
            var period = BillingPeriods.Period(now, price.Interval!.Value, price.IntervalCount!.Value, 0, billing.Value.Zone);
            var unit = CheckoutQuote.Of(await lines.LicensedAsync([spec], period, period, locale, cancellationToken), subscription.Currency, customer.IsTaxExempt);
            offers.Add(new PlanOffer(
                price.Id,
                PriceDisplay.Name(spec.Product.Name, price.Name),
                PriceDisplay.Recurrence(price.Interval.Value.Map<PriceInterval>(), price.IntervalCount.Value, locale),
                unit.Total,
                price.Id == item.PriceId));
        }

        // The quantity they have stays possible, whatever the limits say now.
        return new PlanChoices(
            subscription, quantity, offers, Math.Min(options.QuantityLimits.Minimum, quantity), Math.Max(options.QuantityLimits.Maximum, quantity));
    }

    /// <summary>What switching to <paramref name="priceId"/> with <paramref name="quantity"/> would do (PRT-5, PRT-6).</summary>
    public async Task<Result<PlanChangeQuote>> PreviewAsync(PortalSession session, Guid subscriptionId, Guid priceId, int quantity, string locale, CancellationToken cancellationToken)
    {
        var planned = await PlanAsync(session, subscriptionId, priceId, quantity, locale, cancellationToken);
        return planned.IsFailure ? planned.Error : planned.Value.Quote;
    }

    /// <summary>
    /// Makes the change the customer confirmed (PRT-7), recorded with the button and amounts they were shown. An invoice
    /// the card could not pay is paid on its own page.
    /// </summary>
    public async Task<Result<PlanChanged>> ConfirmAsync(
        PortalSession session, Guid subscriptionId, Guid priceId, int quantity, string locale, IPAddress? address, string? userAgent, CancellationToken cancellationToken)
    {
        var planned = await PlanAsync(session, subscriptionId, priceId, quantity, locale, cancellationToken);
        if (planned.IsFailure)
        {
            return planned.Error;
        }

        var (quote, request) = planned.Value;

        // Saved with the change, in its transaction.
        context.ConsentRecords.Add(ConsentRecord.ForPortal(
            session.CustomerId,
            ConsentType.PlanChange,
            quote.ButtonText,
            new Dictionary<string, string>
            {
                ["price"] = priceId.ToString(),
                ["plan"] = quote.Offer.Name,
                ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
                ["effectiveAt"] = quote.EffectiveAt.ToString("O", CultureInfo.InvariantCulture),
                ["dueNow"] = quote.DueNow.ToString(CultureInfo.InvariantCulture),
                ["credit"] = quote.Credit.ToString(CultureInfo.InvariantCulture),
                ["nextRenewal"] = quote.NextRenewal?.Amount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["currency"] = quote.Currency,
            },
            session.Id,
            subscriptionId,
            address,
            userAgent,
            clock.GetUtcNow()));
        var changed = await updates.HandleAsync(subscriptionId, request, null, cancellationToken);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        var invoice = quote.Now ? changed.Value.Value.LatestInvoice : null;
        return new PlanChanged(invoice is { Status: InvoiceStatus.Open, AmountDue: > 0 } ? invoice.HostedUrl : null);
    }

    private async Task<Result<(PlanChangeQuote Quote, UpdateSubscriptionRequest Request)>> PlanAsync(
        PortalSession session, Guid subscriptionId, Guid priceId, int quantity, string locale, CancellationToken cancellationToken)
    {
        if (await ChoicesAsync(session, subscriptionId, locale, cancellationToken) is not { } choices
            || choices.Offers.FirstOrDefault(candidate => candidate.PriceId == priceId) is not { } offer
            || quantity < choices.MinimumQuantity || quantity > choices.MaximumQuantity)
        {
            return PortalPlanErrors.NotOffered;
        }

        if (offer.IsCurrent && quantity == choices.Quantity)
        {
            return PortalPlanErrors.NothingChanges;
        }

        var subscription = choices.Subscription;
        var now = clock.GetUtcNow();
        var after = await lines.PricedAsync([(priceId, quantity)], cancellationToken);
        var upgrade = await changes.RaisesAmountAsync(subscription, after, now, cancellationToken);
        var price = after[0].Price;

        // An upgrade is charged now; a downgrade waits for the period's end, unless configured, or unless the interval
        // changes, which a renewal cannot do (PRT-6).
        var immediately = upgrade || portal.Value.DowngradesNow || price.Interval != subscription.Interval || price.IntervalCount != subscription.IntervalCount;
        var request = new UpdateSubscriptionRequest
        {
            Items = [new SubscriptionItemRequest { Price = priceId.ToString(), Quantity = quantity }],
            Effective = immediately ? ChangeTiming.Now : ChangeTiming.PeriodEnd,
            ProrationBehavior = ProrationBehavior.AlwaysInvoice,
        };
        var preview = await previews.HandleAsync(subscriptionId, request, cancellationToken);
        if (preview.IsFailure)
        {
            return preview.Error;
        }

        var effectiveAt = immediately ? now : subscription.CurrentPeriodEnd;
        var due = preview.Value.Invoice?.AmountDue ?? 0;
        var credit = preview.Value.Invoice is { Total: < 0 } credited ? -credited.Total : 0;
        var culture = Formatting.CultureFor(locale);
        var exponent = Currency.TryGet(subscription.Currency, out var currency) ? currency.Exponent : 2;
        var button = PortalTexts.PlanChangeButton(
            upgrade,
            immediately,
            due > 0 ? Formatting.Money(due, subscription.Currency, exponent, culture) : null,
            Formatting.Date(effectiveAt, billing.Value.Zone, culture),
            locale);
        return (new PlanChangeQuote(offer, quantity, upgrade, immediately, effectiveAt, due, credit, preview.Value.NextRenewal, subscription.Currency, button), request);
    }
}

/// <summary>
/// The portal's texts that customers confirm. They are recorded exactly as shown (PRT-7, PRT-9), so they live here, not
/// in the page's translations.
/// </summary>
public static class PortalTexts
{
    /// <summary>The button of a plan change names the obligation, as the checkout's does (PRT-7).</summary>
    public static string PlanChangeButton(bool upgrade, bool now, string? dueNow, string date, string locale)
    {
        var turkish = locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        return (now, dueNow, turkish) switch
        {
            (true, { } amount, true) => upgrade ? $"{amount} öde ve planı yükselt" : $"{amount} öde ve planı değiştir",
            (true, { } amount, false) => upgrade ? $"Pay {amount} and upgrade" : $"Pay {amount} and change plan",
            (true, null, true) => "Planı şimdi değiştir",
            (true, null, false) => "Change plan now",
            (false, _, true) => $"Planı {date} tarihinde değiştir",
            _ => $"Change plan on {date}",
        };
    }
}
