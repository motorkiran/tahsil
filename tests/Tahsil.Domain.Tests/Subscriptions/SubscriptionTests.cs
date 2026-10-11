using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Domain.Tests.Subscriptions;

/// <summary>The subscription aggregate: creation, the state machine of DOM §7.3 and <c>next_cycle_at</c> (ENG-10).</summary>
public sealed class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeSpan IncompleteExpiry = TimeSpan.FromHours(23);
    private static readonly Product Pro = Product.Create("pro", "Pro", null, null, null, Now).Value;
    private static readonly Price Monthly = Recurring("pro-monthly-try");

    public enum Start
    {
        Incomplete,
        IncompleteExpired,
        Trialing,
        TrialEnded,
        Active,
        PastDue,
        Canceled,
    }

    public enum Operation
    {
        Activate,
        ExpireIncomplete,
        EndTrial,
        Renew,
        MarkPastDue,
        ScheduleCancellation,
        Cancel,
        Resume,
    }

    /// <summary>Every operation from every status: what is allowed (DOM §7.3, DOM-12) and the status it leads to.</summary>
    public static TheoryData<Start, Operation, SubscriptionStatus?> Transitions()
    {
        var allowed = new Dictionary<(Start, Operation), SubscriptionStatus>
        {
            [(Start.Incomplete, Operation.Activate)] = SubscriptionStatus.Active,
            [(Start.Incomplete, Operation.ExpireIncomplete)] = SubscriptionStatus.IncompleteExpired,
            [(Start.Trialing, Operation.EndTrial)] = SubscriptionStatus.Trialing,
            [(Start.Trialing, Operation.ScheduleCancellation)] = SubscriptionStatus.Trialing,
            [(Start.Trialing, Operation.Cancel)] = SubscriptionStatus.Canceled,
            [(Start.TrialEnded, Operation.Activate)] = SubscriptionStatus.Active,
            [(Start.TrialEnded, Operation.Renew)] = SubscriptionStatus.Trialing,
            [(Start.TrialEnded, Operation.MarkPastDue)] = SubscriptionStatus.PastDue,
            [(Start.TrialEnded, Operation.ScheduleCancellation)] = SubscriptionStatus.Trialing,
            [(Start.TrialEnded, Operation.Cancel)] = SubscriptionStatus.Canceled,
            [(Start.Active, Operation.Renew)] = SubscriptionStatus.Active,
            [(Start.Active, Operation.MarkPastDue)] = SubscriptionStatus.PastDue,
            [(Start.Active, Operation.ScheduleCancellation)] = SubscriptionStatus.Active,
            [(Start.Active, Operation.Cancel)] = SubscriptionStatus.Canceled,
            [(Start.PastDue, Operation.Activate)] = SubscriptionStatus.Active,
            [(Start.PastDue, Operation.Renew)] = SubscriptionStatus.PastDue,
            [(Start.PastDue, Operation.ScheduleCancellation)] = SubscriptionStatus.PastDue,
            [(Start.PastDue, Operation.Cancel)] = SubscriptionStatus.Canceled,
        };
        var data = new TheoryData<Start, Operation, SubscriptionStatus?>();
        foreach (var start in Enum.GetValues<Start>())
        {
            foreach (var operation in Enum.GetValues<Operation>())
            {
                data.Add(start, operation, allowed.TryGetValue((start, operation), out var to) ? to : null);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    [Trait("Requirement", "DOM-12")]
    [Trait("Requirement", "ENG-10")]
    public void EveryTransition_IsAllowedOrRefused_AndKeepsNextCycleAt(Start start, Operation operation, SubscriptionStatus? expected)
    {
        var subscription = In(start);
        var before = subscription.Status;

        var result = Apply(subscription, operation);

        if (expected is { } to)
        {
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            subscription.Status.ShouldBe(to);
        }
        else
        {
            result.Error.ShouldNotBeNull().Code.ShouldBeOneOf("subscription.invalid_state", "subscription.not_resumable");
            subscription.Status.ShouldBe(before);
        }

        subscription.NextCycleAt.ShouldBe(NextCycleAt(subscription));
    }

    [Fact]
    [Trait("Requirement", "SUB-6")]
    [Trait("Requirement", "SUB-10")]
    public void ChargedAutomatically_ItStartsIncomplete_ForAPeriodFromNow()
    {
        var subscription = Create(new SubscriptionTerms()).Value;

        (subscription.Status, subscription.BillingCycleAnchor, subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd)
            .ShouldBe((SubscriptionStatus.Incomplete, Now, Now, Instant(2026, 2, 28)));
        subscription.IncompleteExpiresAt.ShouldBe(Now + IncompleteExpiry);
        subscription.NextCycleAt.ShouldBe(Now + IncompleteExpiry, "an incomplete subscription only expires");
        subscription.Items.ShouldHaveSingleItem().Quantity.ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "SUB-7")]
    public void SendingInvoices_ItIsActiveAtOnce()
    {
        var subscription = Create(new SubscriptionTerms { CollectionMethod = CollectionMethod.SendInvoice, DaysUntilDue = 14 }).Value;

        (subscription.Status, subscription.IncompleteExpiresAt, subscription.NextCycleAt).ShouldBe((SubscriptionStatus.Active, null, Instant(2026, 2, 28)));
    }

    [Fact]
    [Trait("Requirement", "SUB-3")]
    [Trait("Requirement", "SUB-4")]
    [Trait("Requirement", "SUB-10")]
    public void WithATrial_ThePeriodIsTheTrial_AndTheAnchorItsEnd()
    {
        var trialEnd = Now.AddDays(14);

        var subscription = Create(new SubscriptionTerms { TrialEnd = trialEnd }).Value;

        (subscription.Status, subscription.TrialStart, subscription.TrialEnd, subscription.BillingCycleAnchor).ShouldBe((SubscriptionStatus.Trialing, Now, trialEnd, trialEnd));
        (subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd, subscription.NextCycleAt).ShouldBe((Now, trialEnd, trialEnd));
        subscription.EndTrial(Utc, trialEnd).Value.ShouldBe(new Domain.Money.BillingPeriod(trialEnd, trialEnd.AddMonths(1)));
        subscription.TrialEnded.ShouldBeTrue();
    }

    [Fact]
    [Trait("Requirement", "SUB-4")]
    public void ALaterAnchor_MakesAShortFirstPeriod_ThenFullPeriodsFromTheAnchor()
    {
        var anchor = Instant(2026, 2, 1, 0);

        var subscription = Create(new SubscriptionTerms { BillingCycleAnchor = anchor, CollectionMethod = CollectionMethod.SendInvoice, DaysUntilDue = 7 }).Value;
        var next = subscription.Renew(Utc, anchor).Value;

        (subscription.BillingCycleAnchor, next.Start, next.End).ShouldBe((anchor, anchor, Instant(2026, 3, 1, 0)));
    }

    [Theory]
    [Trait("Requirement", "SUB-4")]
    [InlineData(0, null, "billingCycleAnchor")]
    [InlineData(-1, null, "billingCycleAnchor")]
    [InlineData(30, null, "billingCycleAnchor")]
    [InlineData(5, 3, "billingCycleAnchor")]
    [InlineData(null, 0, "trialEnd")]
    public void Anchors_AndTrialEnds_AreChecked(int? anchorDays, int? trialDays, string field)
    {
        var terms = new SubscriptionTerms { BillingCycleAnchor = anchorDays is { } a ? Now.AddDays(a) : null, TrialEnd = trialDays is { } t ? Now.AddDays(t) : null };

        Create(terms).Error.ShouldNotBeNull().Field.ShouldBe(field);
    }

    [Theory]
    [InlineData(CollectionMethod.SendInvoice, null)]
    [InlineData(CollectionMethod.SendInvoice, 0)]
    [InlineData(CollectionMethod.SendInvoice, 366)]
    [InlineData(CollectionMethod.ChargeAutomatically, 7)]
    public void DaysUntilDue_GoWithSentInvoicesOnly(CollectionMethod method, int? days) =>
        Create(new SubscriptionTerms { CollectionMethod = method, DaysUntilDue = days }).Error.ShouldBe(SubscriptionErrors.InvalidDaysUntilDue);

    public static TheoryData<string, string> InvalidItemSets() => new()
    {
        { "none", "items" },
        { "too many", "items" },
        { "one-time", "items[0]" },
        { "archived price", "items[0]" },
        { "archived product", "items[0]" },
        { "another currency", "items[1]" },
        { "another interval", "items[1]" },
        { "twice", "items[1]" },
        { "metered with a quantity", "items[0]" },
        { "no quantity", "items[0]" },
    };

    [Theory]
    [MemberData(nameof(InvalidItemSets))]
    [Trait("Requirement", "DOM-11")]
    [Trait("Requirement", "SUB-1")]
    public void InvalidItemSets_AreRefused(string set, string field)
    {
        var archivedProduct = Product.Create("old", "Old", null, null, null, Now).Value;
        var ofArchivedProduct = Recurring("old-monthly", product: archivedProduct);
        archivedProduct.Update("Old", null, null, isArchived: true, null, Now).IsSuccess.ShouldBeTrue();
        var archivedPrice = Recurring("archived");
        archivedPrice.Update("archived", null, null, isArchived: true, null, Now).IsSuccess.ShouldBeTrue();
        SubscriptionItemSpec[] items = set switch
        {
            "none" => [],
            "too many" => [.. Enumerable.Range(0, 21).Select(i => new SubscriptionItemSpec(Recurring($"p{i}"), Pro, 1))],
            "one-time" => [new(Price.Create(new PriceDraft { Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 100 }, Pro, null, null, Now).Value, Pro, 1)],
            "archived price" => [new(archivedPrice, Pro, 1)],
            "archived product" => [new(ofArchivedProduct, archivedProduct, 1)],
            "another currency" => [new(Monthly, Pro, 1), new(Recurring("pro-monthly-usd", currency: "USD"), Pro, 1)],
            "another interval" => [new(Monthly, Pro, 1), new(Recurring("pro-yearly-try", PriceInterval.Year), Pro, 1)],
            "twice" => [new(Monthly, Pro, 1), new(Monthly, Pro, 2)],
            "metered with a quantity" => [new(Metered(), Pro, 1)],
            _ => [new(Monthly, Pro, 0)],
        };

        var result = Subscription.Create(Guid.CreateVersion7(Now), items, new SubscriptionTerms(), Utc, IncompleteExpiry, Now);

        result.Error.ShouldNotBeNull().Code.ShouldBe("subscription.invalid_items");
        result.Error.Field.ShouldBe(field);
    }

    [Fact]
    [Trait("Requirement", "DOM-11")]
    public void MeteredItems_TakeNoQuantity_LicensedOnesDefaultToOne()
    {
        var subscription = Subscription.Create(
            Guid.CreateVersion7(Now), [new(Monthly, Pro, null), new(Metered(), Pro, null)], new SubscriptionTerms(), Utc, IncompleteExpiry, Now).Value;

        subscription.Items.Select(item => item.Quantity).ShouldBe([1, null]);
    }

    [Theory]
    [Trait("Requirement", "SUB-3")]
    [InlineData("2026-02-10T00:00Z", 7, "2026-02-10T00:00Z")]
    [InlineData(null, 7, "2026-02-07T10:00Z")]
    [InlineData(null, 0, null)]
    [InlineData(null, null, "2026-02-14T10:00Z")]
    public void TheTrial_IsTheEnd_ElseTheDays_ElseThePricesLongest(string? trialEnd, int? trialDays, string? expected)
    {
        Price[] prices = [Recurring("short", trialDays: 3), Recurring("long", trialDays: 14), Recurring("none")];

        var resolved = SubscriptionTrial.Resolve(trialEnd is null ? null : DateTimeOffset.Parse(trialEnd, System.Globalization.CultureInfo.InvariantCulture), trialDays, prices, Utc, Now);

        resolved.ShouldBe(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Requirement", "SUB-3")]
    public void TrialDays_CountDaysOfTheBillingTimeZone_AcrossDaylightSavingTime()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var beforeTheGap = new DateTimeOffset(2026, 3, 27, 11, 0, 0, TimeSpan.Zero);

        SubscriptionTrial.Resolve(null, 3, [], berlin, beforeTheGap).ShouldBe(new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero), "noon stays noon");
    }

    [Fact]
    [Trait("Requirement", "SUB-10")]
    [Trait("Requirement", "SUB-11")]
    [Trait("Requirement", "ENG-9")]
    public void Renewals_FollowTheAnchor_OnePeriodAtATime_EvenWhenBehind()
    {
        var subscription = Create(new SubscriptionTerms { CollectionMethod = CollectionMethod.SendInvoice, DaysUntilDue = 7 }).Value;
        var muchLater = Instant(2027, 3, 1);

        var periods = Enumerable.Range(0, 13).Select(_ => subscription.Renew(Utc, muchLater).Value).ToList();

        periods.Select(period => period.Start.Date).ShouldBe(
        [
            new(2026, 2, 28), new(2026, 3, 31), new(2026, 4, 30), new(2026, 5, 31), new(2026, 6, 30), new(2026, 7, 31), new(2026, 8, 31),
            new(2026, 9, 30), new(2026, 10, 31), new(2026, 11, 30), new(2026, 12, 31), new(2027, 1, 31), new(2027, 2, 28),
        ]);
        subscription.Renew(Utc, muchLater).Error.ShouldNotBeNull("caught up: the current period has not passed");
        subscription.NextCycleAt.ShouldBe(Instant(2027, 3, 31));
    }

    [Fact]
    [Trait("Requirement", "SUB-12")]
    [Trait("Requirement", "SUB-28")]
    public void ACancellationAtThePeriodEnd_StopsRenewal_AndKeepsItsRequestWhenItComesDue()
    {
        var subscription = In(Start.Active);
        var end = subscription.CurrentPeriodEnd;
        subscription.ScheduleCancellation(end, CancellationReason.RequestedByCustomer, CancellationFeedback.TooExpensive, " too much ", Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        subscription.CancelAtPeriodEnd.ShouldBeTrue();
        subscription.Renew(Utc, end).Error.ShouldNotBeNull();
        subscription.Cancel(end, CancellationReason.RequestedByMerchant, null, null, end).IsSuccess.ShouldBeTrue();

        (subscription.Status, subscription.EndedAt, subscription.CanceledAt, subscription.NextCycleAt).ShouldBe((SubscriptionStatus.Canceled, end, Now.AddDays(1), null));
        (subscription.CancellationReason, subscription.CancellationFeedback, subscription.CancellationComment)
            .ShouldBe((CancellationReason.RequestedByCustomer, CancellationFeedback.TooExpensive, "too much"));
    }

    [Theory]
    [Trait("Requirement", "SUB-30")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(40)]
    public void ACancellationTime_IsInTheFuture_WithinThePeriod(int days) =>
        In(Start.Active).ScheduleCancellation(Now.AddDays(days), CancellationReason.RequestedByMerchant, null, null, Now).Error.ShouldBe(SubscriptionErrors.InvalidCancelAt);

    [Fact]
    [Trait("Requirement", "SUB-33")]
    [Trait("Requirement", "ENG-10")]
    public void Resuming_ClearsTheScheduledCancellation()
    {
        var subscription = In(Start.Active);
        subscription.ScheduleCancellation(Now.AddDays(10), CancellationReason.RequestedByCustomer, CancellationFeedback.Unused, null, Now).IsSuccess.ShouldBeTrue();
        subscription.NextCycleAt.ShouldBe(Now.AddDays(10));

        subscription.Resume(Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        (subscription.CancelAt, subscription.CancelAtPeriodEnd, subscription.CanceledAt, subscription.CancellationReason).ShouldBe((null, false, null, null));
        subscription.NextCycleAt.ShouldBe(subscription.CurrentPeriodEnd);
    }

    [Fact]
    [Trait("Requirement", "SUB-28")]
    public void ATrialCanceledAtItsEnd_IsDueOnce()
    {
        var subscription = In(Start.Trialing);

        subscription.ScheduleCancellation(subscription.TrialEnd!.Value, CancellationReason.RequestedByCustomer, null, null, Now).IsSuccess.ShouldBeTrue();

        (subscription.CancelAtPeriodEnd, subscription.NextCycleAt).ShouldBe((true, subscription.TrialEnd));
    }

    [Fact]
    [Trait("Requirement", "SUB-8")]
    public void AnExpiredIncompleteSubscription_EndsWhenItExpired()
    {
        var subscription = In(Start.Incomplete);
        var expiresAt = subscription.IncompleteExpiresAt!.Value;

        subscription.ExpireIncomplete(expiresAt.AddMinutes(-1)).Error.ShouldNotBeNull("not yet");
        subscription.ExpireIncomplete(expiresAt.AddHours(2)).IsSuccess.ShouldBeTrue();

        (subscription.EndedAt, subscription.CancellationReason).ShouldBe((expiresAt, CancellationReason.IncompleteExpired));
    }

    /// <summary>ENG-10 as specified: the earliest of what can come due, nothing for terminal statuses.</summary>
    private static DateTimeOffset? NextCycleAt(Subscription subscription)
    {
        if (subscription.Status is SubscriptionStatus.Canceled or SubscriptionStatus.IncompleteExpired)
        {
            return null;
        }

        if (subscription.Status == SubscriptionStatus.Incomplete)
        {
            return subscription.IncompleteExpiresAt;
        }

        var trialPending = subscription.Status == SubscriptionStatus.Trialing && subscription.CurrentPeriodStart < subscription.TrialEnd;
        return new[] { trialPending ? subscription.TrialEnd : null, subscription.CancelAt, subscription.ScheduledChange?.EffectiveAt, subscription.CurrentPeriodEnd }.Min();
    }

    private static Subscription In(Start start)
    {
        var sendInvoice = new SubscriptionTerms { CollectionMethod = CollectionMethod.SendInvoice, DaysUntilDue = 14 };
        var trial = new SubscriptionTerms { TrialEnd = Now.AddDays(14) };
        var subscription = Create(start switch
        {
            Start.Incomplete or Start.IncompleteExpired => new SubscriptionTerms(),
            Start.Trialing or Start.TrialEnded => trial,
            _ => sendInvoice,
        }).Value;
        Result step = start switch
        {
            Start.IncompleteExpired => subscription.ExpireIncomplete(Now.AddDays(1)),
            Start.TrialEnded => subscription.EndTrial(Utc, Now.AddDays(14)),
            Start.PastDue => subscription.MarkPastDue(Now.AddDays(1)),
            Start.Canceled => subscription.Cancel(Now.AddDays(1), CancellationReason.RequestedByMerchant, null, null, Now.AddDays(1)),
            _ => Result.Success(),
        };
        step.IsSuccess.ShouldBeTrue(step.Error?.Message);
        return subscription;
    }

    private static Result Apply(Subscription subscription, Operation operation)
    {
        var later = subscription.UpdatedAt.AddMinutes(1);
        return operation switch
        {
            Operation.Activate => subscription.Activate(later),
            Operation.ExpireIncomplete => subscription.ExpireIncomplete(subscription.IncompleteExpiresAt ?? later),
            Operation.EndTrial => subscription.EndTrial(Utc, subscription.TrialEnd ?? later),
            Operation.Renew => subscription.Renew(Utc, subscription.CurrentPeriodEnd),
            Operation.MarkPastDue => subscription.MarkPastDue(later),
            Operation.ScheduleCancellation => subscription.ScheduleCancellation(subscription.CurrentPeriodEnd, CancellationReason.RequestedByCustomer, null, null, later),
            Operation.Cancel => subscription.Cancel(later, CancellationReason.RequestedByMerchant, null, null, later),
            _ => subscription.Resume(later),
        };
    }

    private static Result<Subscription> Create(SubscriptionTerms terms) =>
        Subscription.Create(Guid.CreateVersion7(Now), [new(Monthly, Pro, null)], terms, Utc, IncompleteExpiry, Now);

    private static Price Recurring(
        string key, PriceInterval interval = PriceInterval.Month, string currency = "TRY", int? trialDays = null, Product? product = null) =>
        Price.Create(
            new PriceDraft { Key = key, Currency = currency, Type = PriceType.Recurring, Interval = interval, IntervalCount = 1, UsageType = UsageType.Licensed, UnitAmount = 30_000, TrialDays = trialDays },
            product ?? Pro,
            null,
            null,
            Now).Value;

    private static Price Metered() => Price.Create(
        new PriceDraft { Key = "api-calls", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Metered, UnitAmount = 10 },
        Pro,
        null,
        Meter.Create("api_calls", "API calls", MeterAggregation.Sum, null, Now).Value,
        Now).Value;

    private static DateTimeOffset Instant(int year, int month, int day, int hour = 10) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
