using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Application.Subscriptions;
using Tahsil.Contracts;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Portal;
using InvoiceStatus = Tahsil.Domain.Invoices.InvoiceStatus;
using PaymentMethodStatus = Tahsil.Domain.Customers.PaymentMethodStatus;
using SubscriptionStatus = Tahsil.Domain.Subscriptions.SubscriptionStatus;

namespace Tahsil.Application.Portal;

/// <summary>A subscription as the portal shows it (PRT §2): what it is, and what its next renewal bills.</summary>
/// <param name="Subscription">The subscription, with its items, period, trial, discount and scheduled change.</param>
/// <param name="Upcoming">The next renewal's invoice as it would be now; none when the subscription ends before it.</param>
public sealed record PortalSubscription(SubscriptionResponse Subscription, InvoicePreviewResponse? Upcoming);

/// <summary>An invoice in the portal's list, with its hosted page.</summary>
public sealed record PortalInvoice(Invoice Invoice, Uri? Url);

/// <summary>The portal as one customer sees it: only what is theirs, and only the features that are on (PRT-3).</summary>
public sealed record PortalView(
    PortalSession Session,
    Customer Customer,
    string Locale,
    IReadOnlyList<PortalSubscription> Subscriptions,
    IReadOnlyList<PaymentMethod> Cards,
    IReadOnlyList<PortalInvoice> Invoices,
    PortalOptions Options)
{
    public bool Has(string feature) => Options.IsEnabled(feature);
}

/// <summary>
/// The portal's use cases (PRT §2, §7). Everything is read and changed for the session's customer, as a
/// <see cref="CustomerCaller"/> (PRT-3); a session that is unknown, or expired, shows nothing of anyone.
/// </summary>
public sealed class PortalPages(
    ITahsilDbContext context,
    IPageCaller caller,
    SubscriptionMapper subscriptions,
    SubscriptionPreviews previews,
    InvoiceLinks links,
    IOptions<PortalOptions> options,
    IOptions<ServerOptions> server,
    BillingClock clock)
{
    /// <summary>How many of the customer's latest invoices the overview lists.</summary>
    public const int InvoicesShown = 12;

    /// <summary>The session of a page token, expired or not; none for an unknown token.</summary>
    public async Task<PortalSession?> FindAsync(string token, CancellationToken cancellationToken)
    {
        var hash = PortalTokens.Hash(token);
        return await context.PortalSessions.AsNoTracking().SingleOrDefaultAsync(session => session.TokenHash == hash, cancellationToken);
    }

    public bool IsOpen(PortalSession session) => session.IsOpen(clock.GetUtcNow());

    /// <summary>The page's language: the session's, its customer's, or the installation's default.</summary>
    public async Task<string> LocaleOfAsync(PortalSession session, CancellationToken cancellationToken) =>
        session.Locale
        ?? await context.Customers.AsNoTracking().Where(row => row.Id == session.CustomerId).Select(row => row.Locale).SingleOrDefaultAsync(cancellationToken)
        ?? server.Value.DefaultLocale;

    /// <summary>The overview of an open session: the customer's subscriptions, cards, invoices and details.</summary>
    public async Task<PortalView> ViewAsync(PortalSession session, string locale, CancellationToken cancellationToken)
    {
        caller.Use(new CustomerCaller(session.CustomerId, CustomerSessionKind.Portal, session.Id));
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == session.CustomerId, cancellationToken);
        var portal = options.Value;
        var now = clock.GetUtcNow();

        var shown = new List<PortalSubscription>();
        if (portal.IsEnabled(PortalFeatures.Subscriptions))
        {
            var running = await context.Subscriptions.AsNoTracking().Include(row => row.Items)
                .Where(row => row.CustomerId == customer.Id && row.Status != SubscriptionStatus.Canceled && row.Status != SubscriptionStatus.IncompleteExpired)
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                .ToListAsync(cancellationToken);
            var responses = await subscriptions.ToResponsesAsync(running, cancellationToken);
            for (var index = 0; index < running.Count; index++)
            {
                shown.Add(new PortalSubscription(responses[index], await previews.UpcomingAsync(running[index], customer, null, [], now, cancellationToken)));
            }
        }

        var cards = portal.IsEnabled(PortalFeatures.PaymentMethod)
            ? await context.PaymentMethods.AsNoTracking()
                .Where(method => method.CustomerId == customer.Id && method.Status == PaymentMethodStatus.Active)
                .OrderBy(method => method.CreatedAt).ThenBy(method => method.Id)
                .ToListAsync(cancellationToken)
            : [];

        var invoices = portal.IsEnabled(PortalFeatures.Invoices)
            ? await context.Invoices.AsNoTracking()
                .Where(invoice => invoice.CustomerId == customer.Id && invoice.Status != InvoiceStatus.Draft && invoice.Status != InvoiceStatus.Void)
                .OrderByDescending(invoice => invoice.FinalizedAt).ThenByDescending(invoice => invoice.Id)
                .Take(InvoicesShown)
                .ToListAsync(cancellationToken)
            : [];

        return new PortalView(session, customer, locale, shown, cards, [.. invoices.Select(invoice => new PortalInvoice(invoice, links.UrlFor(invoice)))], portal);
    }
}
