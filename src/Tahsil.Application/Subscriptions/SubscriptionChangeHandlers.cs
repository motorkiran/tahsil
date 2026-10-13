using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Application.Invoices;
using Tahsil.Application.Notifications;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;
using Invoice = Tahsil.Domain.Invoices.Invoice;
using Model = Tahsil.Domain.Subscriptions;
using ModelInvoices = Tahsil.Domain.Invoices;

namespace Tahsil.Application.Subscriptions;

/// <summary>
/// Changes a subscription (SUB §4, §5): its items now or at a renewal, the trial's end, the card, the collection
/// method, a scheduled change and metadata. A change billed at once is collected like a first invoice (SUB-20).
/// </summary>
public sealed class UpdateSubscriptionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IBillingLocks locks,
    IOutbox outbox,
    Dunning dunning,
    SubscriptionChanges changes,
    SubscriptionCycles cycles,
    SubscriptionMapper mapper,
    MerchantPayments payments,
    ChargeableCards cards,
    Redemptions redemptions,
    Kimlik.KimlikPlanRule kimlikPlans,
    IOptions<BillingOptions> billing,
    BillingClock clock)
{
    public async Task<Result<Versioned<SubscriptionResponse>>> HandleAsync(
        Guid id, UpdateSubscriptionRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.RequireOrCustomer(TahsilPermissions.SubscriptionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var metadata = request.Metadata is null ? null : Metadata.Validate(request.Metadata);
        if (metadata is { IsFailure: true })
        {
            return metadata.Error;
        }

        if (request.HasScheduledChange && request.ScheduledChange is { ValueKind: not JsonValueKind.Null })
        {
            return SubscriptionErrors.Invalid("scheduledChange", "Only null, which drops the scheduled change, is accepted.");
        }

        var now = clock.GetUtcNow();
        Invoice? invoice = null;
        Guid? card = null;
        Subscription subscription;
        await using (var transaction = await context.BeginTransactionAsync(cancellationToken))
        {
            await locks.LockSubscriptionAsync(id, cancellationToken);
            if (await context.Subscriptions.Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == id, cancellationToken) is not { } found
                || !authorizer.Owns(found.CustomerId))
            {
                return SubscriptionErrors.NotFound;
            }

            if (Concurrency.Check(found, expectedVersions) is { IsFailure: true } stale)
            {
                return stale.Error;
            }

            subscription = found;
            var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
            var previousStatus = subscription.Status;
            var changed = new List<string>();
            if (request.HasCoupon || request.PromotionCode is not null)
            {
                // Before the items, so that an invoice of the change is discounted by the new coupon.
                if (await ChangeDiscountAsync(subscription, request, cancellationToken) is { IsFailure: true } refused)
                {
                    return refused.Error;
                }

                changed.Add("discount");
            }

            if (request.Items is { } requested)
            {
                var applied = await ChangeItemsAsync(subscription, customer, requested, request, now, cancellationToken);
                if (applied.IsFailure)
                {
                    return applied.Error;
                }

                (invoice, card) = (applied.Value.Invoice, applied.Value.Card);
                changed.Add(applied.Value.Changed);
            }

            if (request.HasScheduledChange && request.Items is null)
            {
                if (subscription.ClearScheduledChange(now) is { IsFailure: true } cleared)
                {
                    return cleared.Error;
                }

                changed.Add("scheduledChange");
            }

            var endsTrialNow = false;
            if (request.TrialEnd is { } trialEndText)
            {
                endsTrialNow = trialEndText == "now";
                if (!endsTrialNow && !DateTimeOffset.TryParse(trialEndText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                {
                    return SubscriptionErrors.Invalid("trialEnd", "The trial end is now or an RFC 3339 timestamp.");
                }

                var trialEnd = endsTrialNow ? now : DateTimeOffset.Parse(trialEndText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                if (trialEnd > now.AddDays(CreateSubscriptionHandler.MaxTrialDays))
                {
                    return SubscriptionErrors.Invalid("trialEnd", $"A trial lasts at most {CreateSubscriptionHandler.MaxTrialDays} days.");
                }

                if (subscription.ChangeTrialEnd(trialEnd, now) is { IsFailure: true } moved)
                {
                    return moved.Error;
                }

                changed.Add("trialEnd");
            }

            if (request.HasDefaultPaymentMethod)
            {
                if (request.DefaultPaymentMethod is { } method && await cards.FindAsync(method, customer.Id, subscription.Currency, cancellationToken) is null)
                {
                    return SubscriptionErrors.PaymentMethodUnusable;
                }

                if (subscription.SetDefaultPaymentMethod(request.DefaultPaymentMethod, now) is { IsFailure: true } set)
                {
                    return set.Error;
                }

                changed.Add("defaultPaymentMethod");
            }

            if (request.CollectionMethod is { } collectionMethod)
            {
                if (subscription.ChangeCollectionMethod(collectionMethod.Map<ModelInvoices.CollectionMethod>(), request.DaysUntilDue, now) is { IsFailure: true } switched)
                {
                    return switched.Error;
                }

                changed.Add("collectionMethod");
            }

            if (metadata is { Value: { } values })
            {
                subscription.UpdateMetadata(values, now);
                changed.Add("metadata");
            }

            if (changed.Count > 0)
            {
                audit.Record(AuditActions.SubscriptionUpdated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
                {
                    ["changed"] = changed,
                    ["status"] = SubscriptionMapper.Wire(subscription.Status),
                    ["previousStatus"] = subscription.Status == previousStatus ? null : SubscriptionMapper.Wire(previousStatus),
                }, customerId: subscription.CustomerId);
            }

            await context.SaveChangesAsync(cancellationToken);
            if (endsTrialNow)
            {
                // Ending the trial now starts the first paid period at once (SUB-14, SUB-17).
                await cycles.ProcessAsync(subscription.Id, now, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (invoice is not null && card is { } cardId)
        {
            // A decline leaves the change applied and the invoice to collect (SUB-20).
            await payments.ChargeAsync(invoice.Id, cardId, cancellationToken);
        }

        if (request.HasDefaultPaymentMethod)
        {
            // Invoices waiting for the customer to act are tried with the new card at once (SUB-26, DUN-8).
            await dunning.RetryWaitingAsync(subscription.CustomerId, subscription.Id, now, cancellationToken);
        }

        return new Versioned<SubscriptionResponse>(await mapper.ToResponseAsync(subscription, cancellationToken), subscription.Version);
    }

    /// <summary>
    /// Replaces the discount with the coupon or code of the request, redeemed for the items it asks for or else the
    /// current ones, or removes it with <c>coupon: null</c> (CPN-10, CPN-14).
    /// </summary>
    private async Task<Result> ChangeDiscountAsync(Subscription subscription, UpdateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (subscription.IsTerminal)
        {
            return SubscriptionErrors.InvalidState(subscription.Status, "given a discount");
        }

        if (request.Coupon is null && request.PromotionCode is null)
        {
            await redemptions.RemoveFromSubscriptionAsync(subscription, cancellationToken);
            return Result.Success();
        }

        var named = await redemptions.FindAsync(request.Coupon, request.PromotionCode, cancellationToken);
        if (named.IsFailure)
        {
            return named.Error;
        }

        var items = await SubscriptionItemRequests.ResolveAsync(
            context, request.Items ?? [.. subscription.Items.Select(item => new SubscriptionItemRequest { Price = item.PriceId.ToString(), Quantity = item.Quantity })], cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        if (await redemptions.ApplyToSubscriptionAsync(subscription, named.Value, items.Value, cancellationToken) is { IsFailure: true } refused)
        {
            return refused;
        }

        // Saved now, so that an invoice of the change finds the discount.
        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Changes the items now or schedules the change; returns the invoice billed now and the card this request charges it to.</summary>
    private async Task<Result<(Invoice? Invoice, Guid? Card, string Changed)>> ChangeItemsAsync(
        Subscription subscription, Customer customer, IReadOnlyList<SubscriptionItemRequest> requested, UpdateSubscriptionRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var items = await SubscriptionItemRequests.ResolveAsync(context, requested, cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        if (await kimlikPlans.CheckAsync(customer.Id, customer.KimlikSubscriber, items.Value, subscription.Id, cancellationToken) is { } kimlikConflict)
        {
            return kimlikConflict;
        }

        var scheduled = request.Effective == ChangeTiming.PeriodEnd || request.EffectiveAt is not null;
        var effectiveAt = scheduled ? request.EffectiveAt ?? subscription.CurrentPeriodEnd : now;
        // The notice protects customers from the merchant's changes (SUB-34); a customer's own change in the portal needs none.
        if (!authorizer.IsCustomer
            && (request.SkipNotice ? customer.Type != Domain.Customers.CustomerType.Company : await changes.NeedsNoticeAsync(subscription, customer, items.Value, effectiveAt, now, cancellationToken)))
        {
            return SubscriptionErrors.PriceIncreaseNoticeRequired;
        }

        if (scheduled)
        {
            var result = subscription.ScheduleChange(items.Value, effectiveAt, billing.Value.Zone, now);
            if (result.IsFailure)
            {
                return result.Error;
            }

            if (!authorizer.IsCustomer && await changes.RaisesPriceForIndividualAsync(subscription, customer, items.Value, now, cancellationToken))
            {
                // The customer is told of the higher price, its date, and how to cancel before it (SUB-34).
                CustomerEmails.Enqueue(outbox, EmailTypes.PriceChange, subscription.Id, $"{subscription.Id}:{effectiveAt.ToUnixTimeSeconds()}");
            }

            return (null, null, "scheduledChange");
        }

        var applied = await changes.ApplyNowAsync(subscription, customer, items.Value, request.ProrationBehavior, now, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        if (applied.Value is { Status: ModelInvoices.InvoiceStatus.Open, CollectionMethod: ModelInvoices.CollectionMethod.ChargeAutomatically } invoice
            && await cards.ForInvoiceAsync(customer, subscription.DefaultPaymentMethodId, subscription.Currency, cancellationToken) is { } chargeable)
        {
            // This request charges it; the payment worker leaves it alone. Without a card, the worker's attempt records why.
            invoice.ClaimPaymentAttempt(now);
            return (invoice, chargeable.Method.Id, "items");
        }

        return (applied.Value, null, "items");
    }
}

/// <summary>Shows what a change would do without doing it (SUB-24): the invoice it creates now, or the pending lines, and the next renewal.</summary>
public sealed class PreviewSubscriptionChangeHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    SubscriptionChanges changes,
    SubscriptionPreviews previews,
    Redemptions redemptions,
    IOptions<BillingOptions> billing,
    BillingClock clock)
{
    public async Task<Result<SubscriptionPreviewResponse>> HandleAsync(Guid id, UpdateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.RequireOrCustomer(TahsilPermissions.SubscriptionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == id, cancellationToken) is not { } subscription
            || !authorizer.Owns(subscription.CustomerId))
        {
            return SubscriptionErrors.NotFound;
        }

        if (request.Items is not { } requested)
        {
            return SubscriptionErrors.Invalid("items", "A preview needs the items of the change.");
        }

        var items = await SubscriptionItemRequests.ResolveAsync(context, requested, cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        PreviewDiscount? discount = null;
        if (request.HasCoupon || request.PromotionCode is not null)
        {
            // The discount the change would give, checked as redeeming it would be, without counting it.
            var given = await DiscountAsync(subscription, request, items.Value, cancellationToken);
            if (given.IsFailure)
            {
                return given.Error;
            }

            discount = given.Value;
        }

        var now = clock.GetUtcNow();
        var zone = billing.Value.Zone;
        if (request.Effective == ChangeTiming.PeriodEnd || request.EffectiveAt is not null)
        {
            // Checked like the change itself, on the subscription read without tracking.
            if (subscription.ScheduleChange(items.Value, request.EffectiveAt ?? subscription.CurrentPeriodEnd, zone, now) is { IsFailure: true } invalid)
            {
                return invalid.Error;
            }

            var renewal = await previews.UpcomingAsync(subscription, customer, null, [], discount, now, cancellationToken);
            return new SubscriptionPreviewResponse { Invoice = null, PendingLines = [], NextRenewal = Renewal(renewal) };
        }

        var plan = await changes.PlanAsync(subscription, items.Value, request.ProrationBehavior, customer.Locale, now, cancellationToken);
        if (subscription.ReplaceItems(items.Value, zone, now) is { IsFailure: true } refused)
        {
            return refused.Error;
        }

        var invoice = plan.Invoiced && plan.Lines.Count > 0
            ? await previews.InvoiceAsync(customer, subscription, ModelInvoices.BillingReason.SubscriptionUpdate, plan.Period, plan.Lines, discount, now, cancellationToken)
            : null;
        var pending = plan.Invoiced ? [] : plan.Lines;
        var next = await previews.UpcomingAsync(subscription, customer, items.Value, pending, invoice is not null && discount is { Once: true } ? new PreviewDiscount(null) : discount, now, cancellationToken);
        return new SubscriptionPreviewResponse
        {
            Invoice = invoice,
            PendingLines = [.. pending.Select(line => new InvoiceLineResponse(
                Guid.Empty, line.Type.Map<InvoiceLineType>(), line.Description, line.Quantity, line.UnitAmountDecimal, line.Amount, 0, 0, line.Amount, line.PriceId, null, line.TaxRateId,
                null, null, line.PeriodStart, line.PeriodEnd))],
            NextRenewal = Renewal(next),
        };
    }

    private async Task<Result<PreviewDiscount>> DiscountAsync(
        Subscription subscription, UpdateSubscriptionRequest request, IReadOnlyList<SubscriptionItemSpec> items, CancellationToken cancellationToken)
    {
        if (request.Coupon is null && request.PromotionCode is null)
        {
            return new PreviewDiscount(null);
        }

        var named = await redemptions.FindAsync(request.Coupon, request.PromotionCode, cancellationToken);
        if (named.IsFailure)
        {
            return named.Error;
        }

        var allowed = await redemptions.CheckAsync(named.Value.CouponId, named.Value.CodeId, subscription.CustomerId, Redemptions.TargetOf(subscription.Currency, items), cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var duration = await context.Coupons.AsNoTracking().Where(coupon => coupon.Id == named.Value.CouponId).Select(coupon => coupon.Duration).SingleAsync(cancellationToken);
        return new PreviewDiscount(allowed.Value.Discount, duration == Domain.Coupons.CouponDuration.Once);
    }

    private static UpcomingRenewalResponse? Renewal(InvoicePreviewResponse? renewal) =>
        renewal is { PeriodStart: { } at } ? new UpcomingRenewalResponse(at, renewal.Total) : null;
}

/// <summary>The next renewal invoice of a subscription as things stand (SUB-24, INV §8).</summary>
public sealed class UpcomingInvoiceHandler(ITahsilDbContext context, IAuthorizer authorizer, SubscriptionPreviews previews, BillingClock clock)
{
    public static readonly Error NoUpcoming = Error.NotFound("invoice.no_upcoming", "The subscription has no next renewal: it ended or ends with its current period.");

    public async Task<Result<InvoicePreviewResponse>> HandleAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == subscriptionId, cancellationToken) is not { } subscription)
        {
            return SubscriptionErrors.NotFound;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        return await previews.UpcomingAsync(subscription, customer, null, [], clock.GetUtcNow(), cancellationToken) is { } upcoming ? upcoming : NoUpcoming;
    }
}

/// <summary>
/// The discount a preview uses instead of the subscription's own: the one a change gives, or none when it removes it;
/// <paramref name="Once"/> when the first invoice it is finalized with uses it up (CPN-12).
/// </summary>
public sealed record PreviewDiscount(ModelInvoices.InvoiceDiscount? Discount, bool Once = false);

/// <summary>Invoices built in memory, as they would be created now (SUB-24).</summary>
public sealed class SubscriptionPreviews(
    ITahsilDbContext context, SubscriptionLines lines, InvoiceLines invoiceLines, InvoiceFinalizer finalizer, Redemptions redemptions, IOptions<BillingOptions> billing)
{
    public Task<InvoicePreviewResponse> InvoiceAsync(
        Customer customer, Subscription subscription, ModelInvoices.BillingReason reason, BillingPeriod period, IReadOnlyList<ModelInvoices.InvoiceLineDraft> drafts, DateTimeOffset now, CancellationToken cancellationToken) =>
        InvoiceAsync(customer, subscription, reason, period, drafts, null, now, cancellationToken);

    /// <summary>An invoice of <paramref name="drafts"/>, discounted by <paramref name="discount"/> when given, else by the subscription's discount.</summary>
    public async Task<InvoicePreviewResponse> InvoiceAsync(
        Customer customer,
        Subscription subscription,
        ModelInvoices.BillingReason reason,
        BillingPeriod period,
        IReadOnlyList<ModelInvoices.InvoiceLineDraft> drafts,
        PreviewDiscount? discount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var invoice = Invoice.CreateDraft(
            customer.Id, subscription.Currency, reason, subscription.CollectionMethod, subscription.DaysUntilDue, memo: null, autoAdvance: false, Metadata.Empty, now, subscription.Id, period,
            discount: discount is { } given ? given.Discount : await redemptions.OfSubscriptionAsync(subscription.DiscountId, cancellationToken)).Value;
        invoice.AddLines(drafts, customer.IsTaxExempt, now);
        return InvoiceMapper.Preview(invoice, await finalizer.PreviewBalanceAsync(customer.Id, subscription.Currency, invoice.Total, subscription.CollectionMethod, cancellationToken));
    }

    /// <summary>
    /// The next renewal invoice (INV-1): the items at that renewal (<paramref name="items"/>, or a scheduled change that
    /// takes effect then, or the current ones) for the period that starts then, and the subscription's pending items
    /// with <paramref name="pending"/>. None when the subscription ends before.
    /// </summary>
    public Task<InvoicePreviewResponse?> UpcomingAsync(
        Subscription subscription,
        Customer customer,
        IReadOnlyList<SubscriptionItemSpec>? items,
        IReadOnlyList<ModelInvoices.InvoiceLineDraft> pending,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpcomingAsync(subscription, customer, items, pending, null, now, cancellationToken);

    /// <inheritdoc cref="UpcomingAsync(Subscription, Customer, IReadOnlyList{SubscriptionItemSpec}?, IReadOnlyList{ModelInvoices.InvoiceLineDraft}, DateTimeOffset, CancellationToken)"/>
    public async Task<InvoicePreviewResponse?> UpcomingAsync(
        Subscription subscription,
        Customer customer,
        IReadOnlyList<SubscriptionItemSpec>? items,
        IReadOnlyList<ModelInvoices.InvoiceLineDraft> pending,
        PreviewDiscount? discount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (subscription.IsTerminal || subscription.Status == Model.SubscriptionStatus.Incomplete || subscription.CancelAt <= subscription.CurrentPeriodEnd)
        {
            return null;
        }

        var zone = billing.Value.Zone;
        var next = subscription.Status == Model.SubscriptionStatus.Trialing && !subscription.TrialEnded
            ? BillingPeriods.Period(subscription.BillingCycleAnchor, subscription.Interval, subscription.IntervalCount, 0, zone)
            : BillingPeriods.PeriodAt(subscription.BillingCycleAnchor, subscription.Interval, subscription.IntervalCount, subscription.CurrentPeriodEnd, zone).Period;
        items ??= subscription.ScheduledChange is { } change && change.EffectiveAt == next.Start
            ? await lines.PricedAsync(change.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken)
            : await lines.PricedAsync(subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken);
        var drafts = await lines.LicensedAsync(items, next, next, customer.Locale, cancellationToken);
        var waiting = await context.PendingInvoiceItems.AsNoTracking()
            .Where(item => item.SubscriptionId == subscription.Id && item.InvoiceId == null && item.Currency == subscription.Currency)
            .InJoiningOrder()
            .ToListAsync(cancellationToken);
        drafts.AddRange(await invoiceLines.FromPendingItemsAsync(waiting, customer.Locale, cancellationToken));
        drafts.AddRange(pending);
        return drafts.Count == 0 ? null : await InvoiceAsync(customer, subscription, ModelInvoices.BillingReason.SubscriptionCycle, next, drafts, discount, now, cancellationToken);
    }
}
