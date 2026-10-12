using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Application.Subscriptions;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Common;
using Tahsil.Domain.Coupons;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Payments;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Checkout;

/// <summary>
/// Completes a checkout session when its payment succeeds (CHK-23), in the transaction that applies the outcome, so
/// whichever path brings the success (return, notification, reconciliation) completes it once: a paid invoice of the
/// quote's lines for <c>payment</c>, the subscription with its paid first invoice for <c>subscription</c>, the saved
/// card for <c>setup</c>, then <c>checkout.completed</c>. A trial without a card completes when the form is submitted
/// (CHK-25).
/// </summary>
public sealed class CheckoutCompletion(
    ITahsilDbContext context,
    IBillingLocks locks,
    InvoiceFinalizer finalizer,
    InvoiceLines invoiceLines,
    SubscriptionLines subscriptionLines,
    SubscriptionSettlement settlement,
    CheckoutDiscounts discounts,
    Redemptions redemptions,
    IAuditLog audit,
    IOutbox outbox,
    Attention attention,
    IOptions<BillingOptions> billing,
    IOptions<SubscriptionOptions> subscriptions)
{
    /// <summary>Called with the payment and its lock held, after the payment succeeded and its card was saved.</summary>
    public async Task CompleteAsync(Payment payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessionId = payment.CheckoutSessionId ?? throw new ArgumentException("The payment is not a checkout's.", nameof(payment));
        await locks.LockCheckoutSessionAsync(sessionId, cancellationToken);
        var session = await context.CheckoutSessions.SingleAsync(candidate => candidate.Id == sessionId, cancellationToken);
        if (session.Status != CheckoutStatus.Open)
        {
            if (session.PaymentId != payment.Id && payment.Amount > 0)
            {
                // PAY-14: the session expired or another attempt completed it; nothing needs this money.
                await attention.RaiseAsync(AttentionKind.LateSuccess, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
                {
                    ["checkoutSessionId"] = session.Id.ToString(),
                    ["sessionStatus"] = session.Status.ToString(),
                }, now, cancellationToken);
                outbox.Enqueue(new RefundLateSuccess(payment.Id));
            }

            return;
        }

        if (payment.Purpose == PaymentPurpose.Setup && payment.PaymentMethodId is null)
        {
            // CHK-24: the provider saved no card; the session stays open for another try.
            return;
        }

        Guid? invoiceId = null;
        Guid? subscriptionId = null;
        if (session.Mode == CheckoutMode.Payment)
        {
            invoiceId = await PaidInvoiceAsync(session, payment, now, cancellationToken);
        }
        else if (session.Mode == CheckoutMode.Subscription)
        {
            // CHK-24: without a saved card, the subscription is created anyway; its renewal asks for one (DUN-3).
            (subscriptionId, invoiceId) = await SubscribeAsync(session, payment.CustomerId, payment, now, cancellationToken);
        }

        Finish(session, payment.Id, invoiceId, payment.PaymentMethodId, subscriptionId, now);
    }

    /// <summary>
    /// Completes a subscription session whose trial collects no card, without a payment (CHK-25); called when the form
    /// is submitted, with the session's lock held. Returns the trialing subscription.
    /// </summary>
    public async Task<Guid> CompleteTrialAsync(CheckoutSession session, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (subscriptionId, _) = await SubscribeAsync(session, customerId, null, now, cancellationToken);
        Finish(session, null, null, null, subscriptionId, now);
        return subscriptionId;
    }

    private void Finish(CheckoutSession session, Guid? paymentId, Guid? invoiceId, Guid? paymentMethodId, Guid? subscriptionId, DateTimeOffset now)
    {
        session.Complete(paymentId, invoiceId, paymentMethodId, subscriptionId, now);
        outbox.Enqueue(new Notifications.SendOrderConfirmation(session.Id));
        audit.Record(
            AuditActions.CheckoutCompleted,
            AuditSubject.Of(AuditSubjects.CheckoutSession, session.Id),
            new Dictionary<string, object?>
            {
                ["mode"] = session.Mode.ToString().ToLowerInvariant(),
                ["clientReferenceId"] = session.ClientReferenceId,
                ["subscriptionId"] = subscriptionId,
                ["invoiceId"] = invoiceId,
                ["paymentMethodId"] = paymentMethodId,
            },
            customerId: session.CustomerId);
    }

    /// <summary>
    /// The subscription of a subscription session (CHK-23): trialing from now, or with its first period's invoice of
    /// the recurring and one-time items, paid by <paramref name="payment"/>.
    /// </summary>
    private async Task<(Guid SubscriptionId, Guid? InvoiceId)> SubscribeAsync(
        CheckoutSession session, Guid customerId, Payment? payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var terms = session.Subscription ?? throw new InvalidOperationException("A subscription session describes its subscription.");
        var items = await subscriptionLines.PricedAsync(session.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken);
        var recurring = items.Where(item => item.Price.Type == Domain.Catalog.PriceType.Recurring).ToList();
        var zone = billing.Value.Zone;
        var trialEnd = SubscriptionTrial.Resolve(terms.TrialEnd, terms.TrialDays, recurring.Select(item => item.Price), zone, now);
        var created = Subscription.Create(
            customerId,
            recurring,
            new SubscriptionTerms
            {
                CollectionMethod = terms.CollectionMethod,
                DaysUntilDue = terms.DaysUntilDue,
                DefaultPaymentMethodId = payment?.PaymentMethodId,
                TrialEnd = trialEnd,
                TrialEndBehavior = TrialEndBehavior.Cancel,
                CheckoutSessionId = session.Id,
                Metadata = terms.Metadata,
            },
            zone,
            subscriptions.Value.IncompleteExpiry,
            now);
        if (created.IsFailure)
        {
            // The items were checked when the session was created; a change of the catalog since needs an operator.
            throw new InvalidOperationException($"The subscription of checkout session {session.Id} cannot be created: {created.Error.Message}");
        }

        var subscription = created.Value;
        context.Subscriptions.Add(subscription);
        audit.Record(AuditActions.SubscriptionCreated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["status"] = SubscriptionMapper.Wire(subscription.Status),
            ["items"] = subscription.Items.Select(item => new Dictionary<string, object?> { ["price"] = item.PriceId, ["quantity"] = item.Quantity }).ToList(),
            ["currentPeriodEnd"] = subscription.CurrentPeriodEnd,
            ["trialEnd"] = subscription.TrialEnd,
            ["checkoutSessionId"] = session.Id,
        }, customerId: customerId);
        if (await discounts.RedeemAsync(session, customerId, cancellationToken) is { } redeemed)
        {
            // Counted now (CPN-9); the subscription's invoices take the discount while it lasts.
            var discount = Discount.ForSubscription(redeemed.Coupon, redeemed.Code, customerId, subscription.Id, now);
            redemptions.Add(discount, redeemed);
            subscription.SetDiscount(discount.Id, now);
        }

        if (trialEnd is not null || payment is null)
        {
            return (subscription.Id, null);
        }

        // Saved first, so that what follows the first invoice's payment finds it.
        await context.SaveChangesAsync(cancellationToken);
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, cancellationToken);
        var period = new BillingPeriod(subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd);
        var lines = await subscriptionLines.LicensedAsync(subscription, period, customer.Locale, cancellationToken);
        foreach (var item in items.Where(item => item.Price.Type == Domain.Catalog.PriceType.OneTime))
        {
            lines.Add(await invoiceLines.FromPriceAsync(item.Price, item.Quantity ?? 1, null, customer.Locale, cancellationToken));
        }

        // Collected by the checkout's payment, whatever the subscription's later invoices are.
        var invoice = Invoice.CreateDraft(
            customerId,
            session.Currency,
            BillingReason.SubscriptionCreate,
            CollectionMethod.ChargeAutomatically,
            null,
            null,
            false,
            Metadata.Empty,
            now,
            subscription.Id,
            period,
            discount: await redemptions.OfSubscriptionAsync(subscription.DiscountId, cancellationToken)).Value;
        invoice.AddLines(lines, customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        subscription.SetLatestInvoice(invoice.Id, now);
        await SettleAsync(session, invoice, payment, now, cancellationToken);
        if (invoice.Status == InvoiceStatus.Paid)
        {
            await settlement.InvoicePaidAsync(invoice, now, cancellationToken);
        }

        return (subscription.Id, invoice.Id);
    }

    /// <summary>The invoice of the quote, finalized and paid by the payment (<c>billing_reason = checkout</c>).</summary>
    private async Task<Guid> PaidInvoiceAsync(CheckoutSession session, Payment payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == payment.CustomerId, cancellationToken);
        var invoice = Invoice.CreateDraft(payment.CustomerId, session.Currency, BillingReason.Checkout, CollectionMethod.ChargeAutomatically, null, null, false, session.Metadata, now).Value;
        if (await discounts.RedeemAsync(session, payment.CustomerId, cancellationToken) is { } redeemed)
        {
            // Counted now (CPN-9); the discount is this invoice's alone (CPN-13).
            var discount = Discount.ForInvoice(redeemed.Coupon, redeemed.Code, payment.CustomerId, invoice.Id, now);
            redemptions.Add(discount, redeemed);
            invoice.SetDiscount(Redemptions.ToInvoiceDiscount(redeemed.Coupon, discount.Id, redeemed.Code?.Id), customer.IsTaxExempt, now);
        }

        invoice.AddLines(session.Quote.Drafts(), customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        await SettleAsync(session, invoice, payment, now, cancellationToken);
        return invoice.Id;
    }

    /// <summary>Finalizes a checkout's invoice and applies the payment to it; the payment already paid it, so no balance applies.</summary>
    private async Task SettleAsync(CheckoutSession session, Invoice invoice, Payment payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await finalizer.FinalizeAsync(invoice, cancellationToken, applyBalance: false) is { IsFailure: true } failed)
        {
            throw new InvalidOperationException($"The invoice of checkout session {session.Id} cannot be finalized: {failed.Error.Message}");
        }

        payment.AttachInvoice(invoice.Id, now);
        if (invoice.ApplyPayment(payment.Amount, now).IsFailure || invoice.Status != InvoiceStatus.Paid)
        {
            // The quote changed between the attempt and its success, which the page prevents; an operator settles the difference.
            await attention.RaiseAsync(AttentionKind.AmountMismatch, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
            {
                ["expected"] = $"{invoice.AmountDue.ToString(CultureInfo.InvariantCulture)} {invoice.Currency}",
                ["charged"] = $"{payment.Amount.ToString(CultureInfo.InvariantCulture)} {payment.Currency}",
            }, now, cancellationToken);
            return;
        }

        var paid = InvoiceFinalizer.Details(invoice);
        paid["checkoutSessionId"] = session.Id;
        audit.Record(AuditActions.InvoicePaid, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), paid, customerId: invoice.CustomerId);
    }
}
