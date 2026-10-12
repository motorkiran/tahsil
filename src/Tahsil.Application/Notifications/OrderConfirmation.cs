using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Notifications;

namespace Tahsil.Application.Notifications;

/// <summary>The intent to confirm a completed checkout by email (NTF-3, NTF-11), queued in the completion's transaction.</summary>
[OutboxMessage("notifications.order_confirmation")]
public sealed record SendOrderConfirmation(Guid CheckoutSessionId);

/// <summary>
/// Sends the order confirmation at once after a checkout (NTF-3): what was bought, the merchant's identity, and the
/// legal documents the customer accepted attached, byte for byte the copies the consent records keep. Sent once per
/// session (NTF-12); recorded as skipped when email is off, not configured, or the customer has no address.
/// </summary>
public sealed partial class OrderConfirmationSender(
    ITahsilDbContext context,
    IEmailSender sender,
    IEmailTemplateRenderer templates,
    IOptions<NotificationOptions> notifications,
    IOptions<MerchantOptions> merchant,
    IOptions<CheckoutOptions> checkout,
    IOptions<BillingOptions> billing,
    BillingClock clock,
    ILogger<OrderConfirmationSender> logger) : IOutboxMessageHandler<SendOrderConfirmation>
{
    public async Task HandleAsync(SendOrderConfirmation message, CancellationToken cancellationToken)
    {
        var key = $"{EmailTypes.OrderConfirmation}:{message.CheckoutSessionId}";
        if (await context.NotificationLogs.AnyAsync(log => log.OccurrenceKey == key, cancellationToken))
        {
            return;
        }

        var session = await context.CheckoutSessions.AsNoTracking().SingleAsync(candidate => candidate.Id == message.CheckoutSessionId, cancellationToken);
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == session.CustomerId, cancellationToken);
        var now = clock.GetUtcNow();
        if (Skip() is { } reason)
        {
            context.NotificationLogs.Add(NotificationLog.Skipped(EmailTypes.OrderConfirmation, key, customer.Id, reason, now));
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        var locale = customer.Locale;

        // A trial without a card completed without a payment (CHK-25).
        var payment = session.PaymentId is { } paymentId ? await context.Payments.AsNoTracking().SingleAsync(row => row.Id == paymentId, cancellationToken) : null;
        var invoice = session.InvoiceId is { } invoiceId ? await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId, cancellationToken) : null;
        var card = session.PaymentMethodId is { } methodId ? await context.PaymentMethods.AsNoTracking().SingleAsync(row => row.Id == methodId, cancellationToken) : null;

        // The documents accepted with the attempt that succeeded: recorded with its write-ahead, at its instant.
        var acceptedAt = payment?.CreatedAt ?? session.CompletedAt;
        var documents = await context.ConsentRecords.AsNoTracking()
            .Where(record => record.CheckoutSessionId == session.Id && record.DocumentKey != null && record.AcceptedAt == acceptedAt)
            .OrderBy(record => record.DocumentKey)
            .ToListAsync(cancellationToken);
        var titles = documents.Select(record => LegalDocumentOptions.InLocale(
            checkout.Value.Documents.FirstOrDefault(document => document.Key == record.DocumentKey)?.Title, locale) ?? record.DocumentKey!).ToList();

        var culture = Formatting.CultureFor(locale);
        var exponent = Domain.Money.Currency.TryGet(session.Currency, out var currency) ? currency.Exponent : 2;
        string Money(long amount) => Formatting.Money(amount, session.Currency, exponent, culture);
        var seller = merchant.Value;
        var rendered = await templates.RenderAsync("order-confirmation", locale, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["customer"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = customer.Name },
            ["merchant"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = seller.DisplayName,
                ["legal_name"] = seller.LegalName,
                ["mersis_number"] = seller.MersisNumber,
                ["address"] = seller.Address,
                ["phone"] = seller.Phone,
                ["email"] = seller.Email,
            },
            ["order"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = session.Mode.ToString().ToLowerInvariant(),
                ["number"] = invoice?.Number,
                ["date"] = Formatting.Date(session.CompletedAt ?? now, billing.Value.Zone, culture),
                ["items"] = session.Quote.Lines.Select(line => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["description"] = line.Description,
                    ["amount"] = Money(line.Total),
                }).ToList(),
                ["tax"] = session.Quote.TaxTotal > 0 ? Money(session.Quote.TaxTotal) : null,
                ["total"] = Money(payment?.Amount ?? 0),
                ["card"] = card is null ? null : $"{card.Brand} •••• {card.Last4}",
                ["reference"] = session.ClientReferenceId,
                ["subscription"] = await SubscriptionAsync(session, locale, Money, cancellationToken),
            },
            ["documents"] = titles,
        }, cancellationToken);

        await sender.SendAsync(
            new EmailMessage(
                customer.Email!,
                customer.Name,
                rendered.Subject,
                rendered.Html,
                rendered.Text,
                [.. documents.Select(record => new EmailAttachment(
                    $"{record.DocumentKey}-{record.DocumentVersion}.html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(record.ReadDocument()!)))]),
            cancellationToken);
        context.NotificationLogs.Add(NotificationLog.Sent(EmailTypes.OrderConfirmation, key, customer.Id, now));
        await context.SaveChangesAsync(cancellationToken);

        string? Skip()
        {
            if (!notifications.Value.IsEnabled(EmailTypes.OrderConfirmation))
            {
                return "disabled";
            }

            if (customer.Email is null)
            {
                LogNoAddress(logger, customer.Id);
                return "no_email_address";
            }

            return sender.IsConfigured ? null : "email_not_configured";
        }
    }

    /// <summary>
    /// What a subscription session's email says of the subscription: the plan, what renewals charge and when, the
    /// trial's end, and whether the customer must add a card for the renewal (CHK-24) or is sent invoices.
    /// </summary>
    private async Task<Dictionary<string, object?>?> SubscriptionAsync(Domain.Checkout.CheckoutSession session, string locale, Func<long, string> money, CancellationToken cancellationToken)
    {
        if (session.SubscriptionId is not { } subscriptionId || session.Quote.Recurring is not { } recurring)
        {
            return null;
        }

        var subscription = await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        var priceIds = subscription.Items.Select(item => item.PriceId).ToList();
        var plans = await context.Prices.AsNoTracking()
            .Where(price => priceIds.Contains(price.Id))
            .Join(context.Products.AsNoTracking(), price => price.ProductId, product => product.Id, (_, product) => product.Name)
            .Distinct()
            .ToListAsync(cancellationToken);
        var culture = Formatting.CultureFor(locale);
        var trialEnd = subscription.Status == Domain.Subscriptions.SubscriptionStatus.Trialing ? subscription.TrialEnd : null;
        var automatic = subscription.CollectionMethod == Domain.Invoices.CollectionMethod.ChargeAutomatically;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plan"] = string.Join(", ", plans.Order(StringComparer.Ordinal)),
            ["recurrence"] = Catalog.PriceDisplay.Recurrence(recurring.Interval.Map<Contracts.PriceInterval>(), recurring.IntervalCount, locale),
            ["amount"] = money(recurring.Period.Total),
            ["trial_end"] = trialEnd is { } end ? Formatting.Date(end, billing.Value.Zone, culture) : null,
            ["next_charge"] = Formatting.Date(trialEnd ?? subscription.CurrentPeriodEnd, billing.Value.Zone, culture),
            ["card_missing"] = automatic && subscription.DefaultPaymentMethodId is null,
            ["invoiced"] = !automatic,
        };
    }

    [LoggerMessage(LogLevel.Debug, "Customer {CustomerId} has no email address; their order confirmation is skipped")]
    private static partial void LogNoAddress(ILogger logger, Guid customerId);
}
