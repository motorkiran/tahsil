using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Subscriptions;

public enum SubscriptionStatus
{
    /// <summary>Created through the API while its first invoice is unpaid.</summary>
    Incomplete,

    /// <summary>The first invoice was not paid in time (terminal).</summary>
    IncompleteExpired,

    Trialing,

    Active,

    /// <summary>An invoice is unpaid past its payment or due date.</summary>
    PastDue,

    /// <summary>Ended (terminal).</summary>
    Canceled,
}

public enum TrialEndBehavior
{
    /// <summary>A trial that ends without a payment method cancels the subscription.</summary>
    Cancel,

    /// <summary>A trial that ends without a payment method still invoices, and dunning asks for a card.</summary>
    CreateInvoice,
}

public enum CancellationReason
{
    RequestedByCustomer,
    RequestedByMerchant,
    PaymentFailed,
    CustomerDeleted,
    KimlikSubscriberDeleted,
    IncompleteExpired,
    TrialWithoutPaymentMethod,
}

public enum CancellationFeedback
{
    TooExpensive,
    MissingFeatures,
    SwitchedService,
    Unused,
    CustomerService,
    TooComplex,
    LowQuality,
    Other,
}

/// <summary>An item as it joins a subscription, with its price and product for the rules of DOM-11 and SUB-1.</summary>
public sealed record SubscriptionItemSpec(Price Price, Product Product, int? Quantity);

/// <summary>A change of items to apply at <see cref="EffectiveAt"/> (SUB-19).</summary>
public sealed record ScheduledChange(IReadOnlyList<ScheduledChangeItem> Items, DateTimeOffset EffectiveAt);

public sealed record ScheduledChangeItem(Guid PriceId, int? Quantity);

/// <summary>What a new subscription is created with; the trial end is resolved by <see cref="SubscriptionTrial"/>.</summary>
public sealed record SubscriptionTerms
{
    public CollectionMethod CollectionMethod { get; init; } = CollectionMethod.ChargeAutomatically;

    public int? DaysUntilDue { get; init; }

    public Guid? DefaultPaymentMethodId { get; init; }

    public DateTimeOffset? TrialEnd { get; init; }

    public TrialEndBehavior TrialEndBehavior { get; init; }

    /// <summary>A later anchor without a trial; the time until it is a prorated first period (SUB-4).</summary>
    public DateTimeOffset? BillingCycleAnchor { get; init; }

    public Guid? CheckoutSessionId { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = Common.Metadata.Empty;
}

/// <summary>
/// A customer's recurring purchase of one or more recurring prices (DOM §7). Its periods follow its anchor in the
/// billing time zone (SUB-10), its status follows the state machine of DOM §7.3, and every change keeps
/// <see cref="NextCycleAt"/>, when the subscription worker must look at it next, up to date (ENG-10).
/// </summary>
public sealed class Subscription : AggregateRoot
{
    public const int MaxItems = 20;
    public const int CommentMaxLength = 500;

    private readonly List<SubscriptionItem> _items = [];

    private Subscription()
    {
    }

    public Guid Id { get; private init; }

    public Guid CustomerId { get; private init; }

    public SubscriptionStatus Status { get; private set; }

    public string Currency { get; private init; } = string.Empty;

    /// <summary>The items' interval; every item has the same (DOM-11).</summary>
    public PriceInterval Interval { get; private set; }

    public int IntervalCount { get; private set; }

    public CollectionMethod CollectionMethod { get; private set; }

    public int? DaysUntilDue { get; private set; }

    /// <summary>Falls back to the customer's default.</summary>
    public Guid? DefaultPaymentMethodId { get; private set; }

    public DateTimeOffset BillingCycleAnchor { get; private set; }

    public DateTimeOffset CurrentPeriodStart { get; private set; }

    public DateTimeOffset CurrentPeriodEnd { get; private set; }

    public DateTimeOffset? TrialStart { get; private init; }

    public DateTimeOffset? TrialEnd { get; private set; }

    public TrialEndBehavior TrialEndBehavior { get; private init; }

    public bool CancelAtPeriodEnd { get; private set; }

    public DateTimeOffset? CancelAt { get; private set; }

    /// <summary>When cancellation was requested.</summary>
    public DateTimeOffset? CanceledAt { get; private set; }

    /// <summary>When the subscription actually ended.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    public CancellationReason? CancellationReason { get; private set; }

    public CancellationFeedback? CancellationFeedback { get; private set; }

    public string? CancellationComment { get; private set; }

    public ScheduledChange? ScheduledChange { get; private set; }

    public Guid? LatestInvoiceId { get; private set; }

    /// <summary>The discount its invoices get while it lasts (SUB-9, CPN-11).</summary>
    public Guid? DiscountId { get; private set; }

    public DateTimeOffset? NextCycleAt { get; private set; }

    public DateTimeOffset? PastDueSince { get; private set; }

    public DateTimeOffset? IncompleteExpiresAt { get; private set; }

    public Guid? CheckoutSessionId { get; private init; }

    /// <summary>The trial end the customer was reminded of (SUB-16); a moved trial end is reminded of again.</summary>
    public DateTimeOffset? TrialRemindedFor { get; private set; }

    /// <summary>The renewal the customer was reminded of (NTF-6), as the end of the period it follows.</summary>
    public DateTimeOffset? RenewalRemindedFor { get; private set; }

    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = Common.Metadata.Empty;

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<SubscriptionItem> Items => _items;

    /// <summary>Whether the trial's end was processed and the first paid period started.</summary>
    public bool TrialEnded => TrialEnd is { } end && CurrentPeriodStart >= end;

    public bool IsTerminal => Status is SubscriptionStatus.IncompleteExpired or SubscriptionStatus.Canceled;

    /// <summary>
    /// A new subscription (SUB-3, SUB-4, SUB-6, SUB-7): trialing with a trial; otherwise active when invoices are sent,
    /// or incomplete until its first invoice is paid when it is charged automatically.
    /// </summary>
    public static Result<Subscription> Create(
        Guid customerId, IReadOnlyList<SubscriptionItemSpec> items, SubscriptionTerms terms, TimeZoneInfo zone, TimeSpan incompleteExpiry, DateTimeOffset now)
    {
        if (SubscriptionItems.Validate(items, currency: null) is { } invalid)
        {
            return invalid;
        }

        if (terms.CollectionMethod == CollectionMethod.SendInvoice
            ? terms.DaysUntilDue is not (>= 0 and <= Invoice.MaxDaysUntilDue)
            : terms.DaysUntilDue is not null)
        {
            return SubscriptionErrors.InvalidDaysUntilDue;
        }

        var price = items[0].Price;
        var interval = price.Interval!.Value;
        var count = (int)price.IntervalCount!.Value;
        var oneInterval = BillingPeriods.Start(now, interval, count, 1, zone);
        if (terms.TrialEnd is { } trialEnd && trialEnd <= now)
        {
            return SubscriptionErrors.Invalid("trialEnd", "The trial ends in the future.");
        }

        if (terms.BillingCycleAnchor is { } later && (terms.TrialEnd is not null || later <= now || later > oneInterval))
        {
            return SubscriptionErrors.Invalid(
                "billingCycleAnchor", "A billing cycle anchor is in the future, at most one interval away, and not combined with a trial.");
        }

        var anchor = terms.TrialEnd ?? terms.BillingCycleAnchor ?? now;
        var status = terms.TrialEnd is not null ? SubscriptionStatus.Trialing
            : terms.CollectionMethod == CollectionMethod.SendInvoice ? SubscriptionStatus.Active
            : SubscriptionStatus.Incomplete;
        var id = Guid.CreateVersion7(now);
        var subscription = new Subscription
        {
            Id = id,
            CustomerId = customerId,
            Status = status,
            Currency = price.Currency,
            Interval = interval,
            IntervalCount = count,
            CollectionMethod = terms.CollectionMethod,
            DaysUntilDue = terms.DaysUntilDue,
            DefaultPaymentMethodId = terms.DefaultPaymentMethodId,
            BillingCycleAnchor = anchor,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = anchor > now ? anchor : oneInterval,
            TrialStart = terms.TrialEnd is null ? null : now,
            TrialEnd = terms.TrialEnd,
            TrialEndBehavior = terms.TrialEndBehavior,
            IncompleteExpiresAt = status == SubscriptionStatus.Incomplete ? now + incompleteExpiry : null,
            CheckoutSessionId = terms.CheckoutSessionId,
            Metadata = terms.Metadata,
            CreatedAt = now,
        };
        subscription._items.AddRange(items.Select(item => SubscriptionItem.Create(id, item, now)));
        subscription.Touch(now);
        return subscription;
    }

    /// <summary>The first invoice, or the invoices outstanding, were paid (SUB-8, SUB-14, DUN).</summary>
    public Result Activate(DateTimeOffset now)
    {
        if (Status is not (SubscriptionStatus.Incomplete or SubscriptionStatus.PastDue) && !(Status == SubscriptionStatus.Trialing && TrialEnded))
        {
            return SubscriptionErrors.InvalidState(Status, "activated");
        }

        Status = SubscriptionStatus.Active;
        IncompleteExpiresAt = null;
        PastDueSince = null;
        Touch(now);
        return Result.Success();
    }

    /// <summary>The first invoice was not paid before <see cref="IncompleteExpiresAt"/> (SUB-8); the caller voids it.</summary>
    public Result ExpireIncomplete(DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Incomplete || IncompleteExpiresAt > now)
        {
            return SubscriptionErrors.InvalidState(Status, "expired now");
        }

        Status = SubscriptionStatus.IncompleteExpired;
        EndedAt = IncompleteExpiresAt;
        CancellationReason = Subscriptions.CancellationReason.IncompleteExpired;
        IncompleteExpiresAt = null;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// The trial reached its end and the first paid period starts at it (SUB-14); the status stays trialing until the
    /// period's invoice is paid or its payment fails.
    /// </summary>
    public Result<BillingPeriod> EndTrial(TimeZoneInfo zone, DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Trialing || TrialEnded || TrialEnd > now)
        {
            return SubscriptionErrors.InvalidState(Status, "ending its trial now");
        }

        var period = BillingPeriods.Period(BillingCycleAnchor, Interval, IntervalCount, 0, zone);
        (CurrentPeriodStart, CurrentPeriodEnd) = (period.Start, period.End);
        Touch(now);
        return period;
    }

    /// <summary>Starts the next period when the current one has passed (SUB-11), unless the subscription ends with it.</summary>
    public Result<BillingPeriod> Renew(TimeZoneInfo zone, DateTimeOffset now)
    {
        var renews = Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue || (Status == SubscriptionStatus.Trialing && TrialEnded);
        if (!renews || CurrentPeriodEnd > now || CancelAt <= CurrentPeriodEnd)
        {
            return SubscriptionErrors.InvalidState(Status, "renewed now");
        }

        var (_, period) = BillingPeriods.PeriodAt(BillingCycleAnchor, Interval, IntervalCount, CurrentPeriodEnd, zone);
        (CurrentPeriodStart, CurrentPeriodEnd) = (CurrentPeriodEnd, period.End);
        Touch(now);
        return new BillingPeriod(CurrentPeriodStart, CurrentPeriodEnd);
    }

    /// <summary>A renewal's or the trial's first invoice was not paid (SUB-11, SUB-14, SUB-7).</summary>
    public Result MarkPastDue(DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Active && !(Status == SubscriptionStatus.Trialing && TrialEnded))
        {
            return SubscriptionErrors.InvalidState(Status, "past due");
        }

        Status = SubscriptionStatus.PastDue;
        PastDueSince = now;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// Schedules the end at <paramref name="at"/>, which is in the future and at most the current period's end
    /// (SUB-28, SUB-30); during a trial, the current period is the trial.
    /// </summary>
    public Result ScheduleCancellation(
        DateTimeOffset at, CancellationReason reason, CancellationFeedback? feedback, string? comment, DateTimeOffset now)
    {
        if (Status is not (SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue))
        {
            return SubscriptionErrors.InvalidState(Status, "canceled");
        }

        if (at <= now || at > CurrentPeriodEnd)
        {
            return SubscriptionErrors.InvalidCancelAt;
        }

        if (!Text.FitsOptional(comment, CommentMaxLength))
        {
            return SubscriptionErrors.InvalidComment;
        }

        CancelAt = at;
        CancelAtPeriodEnd = at == CurrentPeriodEnd;
        RecordCancellation(reason, feedback, comment, now);
        Touch(now);
        return Result.Success();
    }

    /// <summary>Clears a scheduled cancellation before it takes effect (SUB-33).</summary>
    public Result Resume(DateTimeOffset now)
    {
        if (IsTerminal || CancelAt is null)
        {
            return SubscriptionErrors.NotResumable;
        }

        CancelAt = null;
        CancelAtPeriodEnd = false;
        CanceledAt = null;
        CancellationReason = null;
        CancellationFeedback = null;
        CancellationComment = null;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// Ends the subscription at <paramref name="endedAt"/>: now, at a scheduled cancellation that came due, or when the
    /// trial ended without a payment method (SUB-15, SUB-29, ENG-8). A scheduled change is dropped.
    /// </summary>
    public Result Cancel(
        DateTimeOffset endedAt, CancellationReason reason, CancellationFeedback? feedback, string? comment, DateTimeOffset now)
    {
        if (Status is not (SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue))
        {
            return SubscriptionErrors.InvalidState(Status, "canceled");
        }

        if (endedAt > now)
        {
            return SubscriptionErrors.InvalidCancelAt;
        }

        if (!Text.FitsOptional(comment, CommentMaxLength))
        {
            return SubscriptionErrors.InvalidComment;
        }

        // A scheduled cancellation that came due keeps the request that scheduled it.
        if (CancelAt != endedAt)
        {
            RecordCancellation(reason, feedback, comment, now);
        }

        Status = SubscriptionStatus.Canceled;
        EndedAt = endedAt;
        ScheduledChange = null;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// Replaces the items now (SUB-18, SUB-19), dropping a scheduled change. Outside a trial, new items with another
    /// interval restart the period at the change, which becomes the anchor (SUB-21, MNY-28); during a trial the trial
    /// continues and the new interval applies after it (SUB-22). Returns whether the period restarted.
    /// </summary>
    public Result<bool> ReplaceItems(IReadOnlyList<SubscriptionItemSpec> items, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (CanChange("changed") is { } invalid)
        {
            return invalid;
        }

        if (ValidateItems(items) is { } invalidItems)
        {
            return invalidItems;
        }

        var price = items[0].Price;
        var interval = price.Interval!.Value;
        var count = (int)price.IntervalCount!.Value;
        var restarts = (interval != Interval || count != IntervalCount) && Status != SubscriptionStatus.Trialing;
        (Interval, IntervalCount) = (interval, count);
        if (restarts)
        {
            BillingCycleAnchor = now;
            (CurrentPeriodStart, CurrentPeriodEnd) = (now, BillingPeriods.Start(now, interval, count, 1, zone));
            if (CancelAtPeriodEnd)
            {
                CancelAt = CurrentPeriodEnd;
            }
        }

        var kept = items.Select(item => item.Price.Id).ToHashSet();
        _items.RemoveAll(item => !kept.Contains(item.PriceId));
        foreach (var spec in items)
        {
            if (_items.Find(item => item.PriceId == spec.Price.Id) is { } existing)
            {
                existing.SetQuantity(spec, now);
            }
            else
            {
                _items.Add(SubscriptionItem.Create(Id, spec, now));
            }
        }

        ScheduledChange = null;
        Touch(now);
        return restarts;
    }

    /// <summary>
    /// Stores a change of items to apply at <paramref name="effectiveAt"/>, a renewal of the subscription (SUB-19); it
    /// replaces any earlier one. The interval cannot change this way (SUB-21).
    /// </summary>
    public Result ScheduleChange(IReadOnlyList<SubscriptionItemSpec> items, DateTimeOffset effectiveAt, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (CanChange("changed") is { } invalid)
        {
            return invalid;
        }

        if (ValidateItems(items) is { } invalidItems)
        {
            return invalidItems;
        }

        if (items[0].Price.Interval != Interval || items[0].Price.IntervalCount != IntervalCount)
        {
            return SubscriptionErrors.IntervalChangeRequiresNow;
        }

        if (effectiveAt < CurrentPeriodEnd || BillingPeriods.PeriodAt(BillingCycleAnchor, Interval, IntervalCount, effectiveAt, zone).Period.Start != effectiveAt
            || (CancelAt is { } cancelAt && effectiveAt >= cancelAt))
        {
            return SubscriptionErrors.InvalidEffectiveAt;
        }

        ScheduledChange = new ScheduledChange([.. items.Select(item => new ScheduledChangeItem(item.Price.Id, item.Price.UsageType == UsageType.Metered ? null : item.Quantity ?? 1))], effectiveAt);
        Touch(now);
        return Result.Success();
    }

    /// <summary>Drops the scheduled change (SUB-19).</summary>
    public Result ClearScheduledChange(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return SubscriptionErrors.InvalidState(Status, "changed");
        }

        ScheduledChange = null;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// Moves the end of a trial that has not ended (SUB-17): later to extend it, or <paramref name="now"/> to end it at
    /// once. A cancellation at the trial's end moves with it.
    /// </summary>
    public Result ChangeTrialEnd(DateTimeOffset trialEnd, DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Trialing || TrialEnded)
        {
            return SubscriptionErrors.InvalidState(Status, "given another trial end");
        }

        if (trialEnd < now)
        {
            return SubscriptionErrors.Invalid("trialEnd", "The trial ends now or later.");
        }

        TrialEnd = BillingCycleAnchor = CurrentPeriodEnd = trialEnd;
        if (CancelAt is not null && (CancelAtPeriodEnd || CancelAt > trialEnd))
        {
            (CancelAt, CancelAtPeriodEnd) = (trialEnd, true);
        }

        Touch(now);
        return Result.Success();
    }

    /// <summary>The card the subscription's invoices are charged to, or the customer's default (SUB-26).</summary>
    public Result SetDefaultPaymentMethod(Guid? paymentMethodId, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return SubscriptionErrors.InvalidState(Status, "changed");
        }

        DefaultPaymentMethodId = paymentMethodId;
        Touch(now);
        return Result.Success();
    }

    /// <summary>Switches how the next invoices are collected, for active subscriptions (SUB-27).</summary>
    public Result ChangeCollectionMethod(CollectionMethod collectionMethod, int? daysUntilDue, DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Active)
        {
            return SubscriptionErrors.InvalidState(Status, "given another collection method");
        }

        if (collectionMethod == CollectionMethod.SendInvoice ? daysUntilDue is not (>= 0 and <= Invoice.MaxDaysUntilDue) : daysUntilDue is not null)
        {
            return SubscriptionErrors.InvalidDaysUntilDue;
        }

        (CollectionMethod, DaysUntilDue) = (collectionMethod, daysUntilDue);
        Touch(now);
        return Result.Success();
    }

    public void RecordTrialReminder(DateTimeOffset now)
    {
        TrialRemindedFor = TrialEnd;
        UpdatedAt = now;
    }

    public void RecordRenewalReminder(DateTimeOffset now)
    {
        RenewalRemindedFor = CurrentPeriodEnd;
        UpdatedAt = now;
    }

    /// <summary>Gives the subscription a discount, which replaces the one it had (CPN-10), or takes it away.</summary>
    public void SetDiscount(Guid? discountId, DateTimeOffset now)
    {
        DiscountId = discountId;
        Touch(now);
    }

    public void SetLatestInvoice(Guid invoiceId, DateTimeOffset now)
    {
        LatestInvoiceId = invoiceId;
        Touch(now);
    }

    public void UpdateMetadata(IReadOnlyDictionary<string, string> metadata, DateTimeOffset now)
    {
        Metadata = metadata;
        Touch(now);
    }

    /// <summary>
    /// When the subscription worker must look at the subscription next (ENG-10): the earliest of what can come due.
    /// An incomplete subscription only expires, since it does not renew before its first invoice is paid.
    /// </summary>
    private DateTimeOffset? ComputeNextCycleAt()
    {
        if (IsTerminal)
        {
            return null;
        }

        if (Status == SubscriptionStatus.Incomplete)
        {
            return IncompleteExpiresAt;
        }

        DateTimeOffset?[] due = [Status == SubscriptionStatus.Trialing && !TrialEnded ? TrialEnd : null, CancelAt, ScheduledChange?.EffectiveAt, CurrentPeriodEnd];
        return due.Min();
    }

    /// <summary>Whether <see cref="ReplaceItems"/> would take these items.</summary>
    public bool CanReplaceItems(IReadOnlyList<SubscriptionItemSpec> items) => CanChange("changed") is null && ValidateItems(items) is null;

    private Error? CanChange(string action) =>
        Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue ? null : SubscriptionErrors.InvalidState(Status, action);

    /// <summary>New items keep the subscription's currency (SUB-18) and follow DOM-11 and SUB-1.</summary>
    private Error? ValidateItems(IReadOnlyList<SubscriptionItemSpec> items) =>
        items.Any(item => item.Price.Currency != Currency) ? SubscriptionErrors.CurrencyMismatch : SubscriptionItems.Validate(items, Currency);

    private void RecordCancellation(CancellationReason reason, CancellationFeedback? feedback, string? comment, DateTimeOffset now)
    {
        CanceledAt = now;
        CancellationReason = reason;
        CancellationFeedback = feedback;
        CancellationComment = Text.Normalize(comment);
    }

    private void Touch(DateTimeOffset now)
    {
        NextCycleAt = ComputeNextCycleAt();
        UpdatedAt = now;
    }
}

/// <summary>A recurring price on a subscription, with its quantity (DOM §7.2).</summary>
public sealed class SubscriptionItem
{
    private SubscriptionItem()
    {
    }

    public Guid Id { get; private init; }

    public Guid SubscriptionId { get; private init; }

    public Guid PriceId { get; private init; }

    /// <summary>At least 1 for licensed prices; <see langword="null"/> for metered prices.</summary>
    public int? Quantity { get; private set; }

    /// <summary>Metered items: the end of the last usage period invoiced (USG-11).</summary>
    public DateTimeOffset? UsageBilledUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The item's usage before <paramref name="until"/> is invoiced: its period is closed to new events (USG-11).</summary>
    public void CloseUsage(DateTimeOffset until, DateTimeOffset now)
    {
        if (UsageBilledUntil is null || UsageBilledUntil < until)
        {
            UsageBilledUntil = until;
            UpdatedAt = now;
        }
    }

    internal void SetQuantity(SubscriptionItemSpec spec, DateTimeOffset now)
    {
        int? quantity = spec.Price.UsageType == UsageType.Metered ? null : spec.Quantity ?? 1;
        if (quantity != Quantity)
        {
            Quantity = quantity;
            UpdatedAt = now;
        }
    }

    internal static SubscriptionItem Create(Guid subscriptionId, SubscriptionItemSpec spec, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        SubscriptionId = subscriptionId,
        PriceId = spec.Price.Id,
        Quantity = spec.Price.UsageType == UsageType.Metered ? null : spec.Quantity ?? 1,
        CreatedAt = now,
        UpdatedAt = now,
    };
}

/// <summary>The rules every item set of a subscription follows (DOM-11, SUB-1).</summary>
public static class SubscriptionItems
{
    /// <summary>Returns the first broken rule, or <see langword="null"/>; <paramref name="currency"/> is the subscription's, if it exists.</summary>
    public static Error? Validate(IReadOnlyList<SubscriptionItemSpec> items, string? currency)
    {
        if (items.Count is 0 or > Subscription.MaxItems)
        {
            return SubscriptionErrors.InvalidItems("items", $"A subscription has 1 to {Subscription.MaxItems} items.");
        }

        var first = items[0].Price;
        currency ??= first.Currency;
        var seen = new HashSet<Guid>();
        for (var i = 0; i < items.Count; i++)
        {
            var (price, product, quantity) = items[i];
            var field = $"items[{i}]";
            if (price.ProductId != product.Id)
            {
                throw new ArgumentException("The product is not the price's.", nameof(items));
            }

            string? broken = price switch
            {
                { Type: not PriceType.Recurring } => "is not a recurring price",
                { IsArchived: true } => "is archived",
                _ when product.IsArchived => "belongs to an archived product",
                _ when price.Currency != currency => $"is not in {currency}",
                _ when price.Interval != first.Interval || price.IntervalCount != first.IntervalCount => "has a different interval than the other items",
                _ when !seen.Add(price.Id) => "is already on the subscription",
                { UsageType: UsageType.Metered } when quantity is not null => "is metered and takes no quantity",
                { UsageType: not UsageType.Metered } when quantity < 1 => "needs a quantity of at least 1",
                _ => null,
            };
            if (broken is not null)
            {
                return SubscriptionErrors.InvalidItems(field, $"The price {price.Key ?? price.Id.ToString()} {broken}.");
            }
        }

        return null;
    }
}

/// <summary>The trial of a new subscription (SUB-3).</summary>
public static class SubscriptionTrial
{
    /// <summary>
    /// <paramref name="trialEnd"/>, else <paramref name="trialDays"/>, else the longest default trial of the items'
    /// prices, counted in days of the billing time zone; zero days means no trial.
    /// </summary>
    public static DateTimeOffset? Resolve(
        DateTimeOffset? trialEnd, int? trialDays, IEnumerable<Price> prices, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (trialEnd is not null)
        {
            return trialEnd;
        }

        var days = trialDays ?? prices.Max(price => (int?)price.TrialDays) ?? 0;
        return days > 0 ? BillingPeriods.Start(now, PriceInterval.Day, days, 1, zone) : null;
    }
}
