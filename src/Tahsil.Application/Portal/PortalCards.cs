using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Invoices;
using Tahsil.Application.PaymentMethods;
using Tahsil.Application.Payments;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;
using Tahsil.Domain.Portal;
using Tahsil.Providers.Abstractions;
using PaymentStatus = Tahsil.Domain.Payments.PaymentStatus;

namespace Tahsil.Application.Portal;

/// <summary>
/// How the customer can add a card (PRT-11): through the connection's setup for <paramref name="Currency"/>, or, when
/// the connection has none, by paying <paramref name="InvoiceUrl"/>; with neither, the card is updated at their next
/// payment.
/// </summary>
public sealed record PortalCardSetup(string? Currency, ProviderConnection? Connection, Uri? InvoiceUrl)
{
    public bool IsAvailable => Connection is not null;
}

/// <summary>How adding a card ended for the page.</summary>
public enum CardAdded
{
    Saved,
    Pending,
    Failed,
}

public static class PortalCardErrors
{
    public static readonly Error NotFound = Error.NotFound("portal.card_not_found", "The card is not one of yours.");

    public static readonly Error IsDefault = Error.Validation("portal.card_is_default", "Make another card the default before removing this one.");

    public static readonly Error SetupUnavailable = Error.Validation("portal.card_setup_unavailable", "A card cannot be added here; it is updated at your next payment.");
}

/// <summary>
/// The customer's cards in the portal (PRT-11): adding one through the provider's own fields with the consent to keep
/// it (CHK-13), which makes it the default (CUS-13) and retries what waited for a card (DUN-8); making another the
/// default; removing the others.
/// </summary>
public sealed class PortalCards(
    ITahsilDbContext context,
    IPageCaller caller,
    CustomerPayments payments,
    ProviderRegistry registry,
    ProviderSources sources,
    DefaultCards defaults,
    RemovePaymentMethodHandler remover,
    InvoiceLinks links,
    IPublicUrls urls,
    IOptions<MerchantOptions> merchant,
    IOptions<PaymentOptions> paymentOptions,
    IOptions<CheckoutPageOptions> limits,
    BillingClock clock)
{
    /// <summary>The text of the box to keep the card, shown and recorded alike.</summary>
    public string ConsentText(string locale) => CheckoutTexts.CardStorage(merchant.Value.DisplayName ?? string.Empty, locale);

    /// <summary>
    /// How a card can be added: in the currency of the flow's subscription, else of the customer's first running
    /// subscription or latest invoice, on the connection the currency is routed to, when it saves cards without a payment.
    /// </summary>
    public async Task<PortalCardSetup> SetupAsync(PortalSession session, CancellationToken cancellationToken)
    {
        var subscriptions = context.Subscriptions.AsNoTracking().Where(row => row.CustomerId == session.CustomerId);
        var currency = await subscriptions.Where(row => row.Id == session.FlowSubscriptionId).Select(row => row.Currency).FirstOrDefaultAsync(cancellationToken)
            ?? await subscriptions
                .Where(row => row.Status != Domain.Subscriptions.SubscriptionStatus.Canceled && row.Status != Domain.Subscriptions.SubscriptionStatus.IncompleteExpired)
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                .Select(row => row.Currency)
                .FirstOrDefaultAsync(cancellationToken)
            ?? await context.Invoices.AsNoTracking().Where(invoice => invoice.CustomerId == session.CustomerId)
                .OrderByDescending(invoice => invoice.CreatedAt).ThenByDescending(invoice => invoice.Id)
                .Select(invoice => invoice.Currency)
                .FirstOrDefaultAsync(cancellationToken);
        if (currency is not null
            && await context.RoutedConnectionAsync(currency, cancellationToken) is { CheckoutsPausedAt: null } connection
            && registry.Find(connection.Provider)?.CapabilitiesFor(connection.Settings) is { SavesCards: not CardSaving.None, Setup: CardSetup.Native })
        {
            return new PortalCardSetup(currency, connection, null);
        }

        // Without a setup, paying the oldest open invoice by card is how the card is updated.
        var open = await context.Invoices.AsNoTracking()
            .Where(invoice => invoice.CustomerId == session.CustomerId && invoice.Status == InvoiceStatus.Open)
            .OrderBy(invoice => invoice.FinalizedAt).ThenBy(invoice => invoice.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return new PortalCardSetup(currency, null, open is null ? null : links.UrlFor(open));
    }

    /// <summary>The provider's sources for the page's content security policy, when the page embeds its fields (PRT-12).</summary>
    public ContentSecuritySources SourcesOf(PortalCardSetup setup) => setup.Connection is { } connection ? sources.Of(connection) : ContentSecuritySources.None;

    /// <summary>
    /// Starts the setup of a new card (PAY-8): the consent is recorded with the attempt, the setup is written ahead and
    /// the provider's fields are shown. Attempts are limited per network like the checkout's (CHK-21).
    /// </summary>
    public async Task<Result<StartedPayment>> AddAsync(
        PortalSession session, string token, bool consent, string locale, IPAddress? address, string? userAgent, CancellationToken cancellationToken)
    {
        var actor = Use(session);
        if (!consent)
        {
            return CheckoutPageErrors.ConsentRequired;
        }

        if (await SetupAsync(session, cancellationToken) is not { Connection: { } connection, Currency: { } currency })
        {
            return PortalCardErrors.SetupUnavailable;
        }

        var now = clock.GetUtcNow();
        var network = CheckoutProtection.NetworkOf(address);
        var hourAgo = now.AddHours(-1);
        if (network is not null
            && await context.Payments.CountAsync(payment => payment.ClientNetwork == network && payment.CreatedAt > hourAgo, cancellationToken) >= limits.Value.MaxAttemptsPerAddressPerHour)
        {
            return Domain.Checkout.CheckoutErrors.AttemptsExceeded;
        }

        var agreed = ConsentRecord.ForPortal(
            session.CustomerId, ConsentType.CardStorage, ConsentText(locale), new Dictionary<string, string> { ["currency"] = currency }, session.Id, null, address, userAgent, now);
        context.ConsentRecords.Add(agreed);
        var payment = Payment.ForCardUpdate(
            session.CustomerId, session.Id, connection.Id, currency, agreed.Id, now + paymentOptions.Value.CustomerActionTimeout, actor.ToActor(), now, network);
        context.Payments.Add(payment);
        await context.SaveChangesAsync(cancellationToken);
        return await payments.StartWrittenAsync(
            payment, connection, urls.Build($"portal/{token}/payment-method/return/{payment.Id}"), urls.Build($"portal/{token}/payment-method"), address?.ToString(), cancellationToken);
    }

    /// <summary>The provider sent the customer back: the setup is completed, which saves the card and makes it the default.</summary>
    public async Task<(CardAdded State, string? FailureCode)?> ReturnAsync(
        PortalSession session, Guid paymentId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        Use(session);
        if (!await context.Payments.AsNoTracking().AnyAsync(
            payment => payment.Id == paymentId && payment.CustomerId == session.CustomerId && payment.PortalSessionId != null,
            cancellationToken))
        {
            return null;
        }

        var completed = await payments.CompleteAsync(paymentId, values, cancellationToken);
        if (completed.IsFailure)
        {
            return null;
        }

        return completed.Value switch
        {
            { Status: PaymentStatus.Succeeded, PaymentMethodId: not null } => (CardAdded.Saved, null),
            { Status: PaymentStatus.Succeeded } => (CardAdded.Failed, CheckoutPageErrors.CardNotSaved.Code),
            { Status: PaymentStatus.Failed or PaymentStatus.Canceled } payment => (CardAdded.Failed, payment.FailureCode),
            _ => (CardAdded.Pending, null),
        };
    }

    /// <summary>Makes another of the customer's cards the default, for them and for the subscriptions that used the old one (CUS-13).</summary>
    public async Task<Result> MakeDefaultAsync(PortalSession session, Guid cardId, CancellationToken cancellationToken)
    {
        Use(session);
        if (await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(method => method.Id == cardId && method.CustomerId == session.CustomerId, cancellationToken)
            is not { IsChargeable: true })
        {
            return PortalCardErrors.NotFound;
        }

        await defaults.ReplaceAsync(session.CustomerId, cardId, clock.GetUtcNow(), cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Removes one of the customer's other cards; the default stays until another replaces it.</summary>
    public async Task<Result> RemoveAsync(PortalSession session, Guid cardId, CancellationToken cancellationToken)
    {
        Use(session);
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == session.CustomerId, cancellationToken);
        if (!await context.PaymentMethods.AsNoTracking().AnyAsync(method => method.Id == cardId && method.CustomerId == customer.Id, cancellationToken))
        {
            return PortalCardErrors.NotFound;
        }

        return customer.DefaultPaymentMethodId == cardId ? PortalCardErrors.IsDefault : await remover.HandleAsync(cardId, cancellationToken);
    }

    private CustomerCaller Use(PortalSession session)
    {
        var customer = new CustomerCaller(session.CustomerId, CustomerSessionKind.Portal, session.Id);
        caller.Use(customer);
        return customer;
    }
}
