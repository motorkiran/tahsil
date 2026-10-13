using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Usage;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using static Tahsil.Server.Tests.Usage.UsageScenarios;

namespace Tahsil.Server.Tests.Usage;

/// <summary>Metered items billed in arrears from usage events (USG-7 to USG-12), on a clock the tests move.</summary>
public sealed class UsageBillingTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "USG-8")]
    [Trait("Requirement", "USG-9")]
    [Trait("Requirement", "USG-10")]
    [Trait("Requirement", "USG-11")]
    [Trait("Requirement", "USG-12")]
    [Trait("Requirement", "INV-1")]
    public async Task UsageIsBilledInArrears_WithLateEventsUntilFinalization_OverThreePeriods()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-base-try", "api-calls-try");
        var start = subscription.CurrentPeriodStart;
        var (second, third) = (subscription.CurrentPeriodEnd, start.AddMonths(2));
        using (var item = await api.PostJsonAsync("/api/v1/invoice-items", new CreateInvoiceItemRequest
        {
            Customer = customer,
            Subscription = subscription.Id,
            Currency = "TRY",
            Description = "Onboarding",
            UnitAmount = 5_000,
        }))
        {
            item.StatusCode.ShouldBe(HttpStatusCode.Created, await item.Content.ReadAsStringAsync(CancellationToken));
        }

        _time.Advance(TimeSpan.FromDays(10));
        (await ReportAsync(api, Event("a", customer, "api_calls", 1_000), Event("b", customer, "api_calls", 500))).Accepted.ShouldBe(2);
        _time.Advance(second - _time.GetUtcNow());
        (await ReportAsync(api, Event("boundary", customer, "api_calls", 300, second))).Accepted.ShouldBe(1);
        await host.RunWorkersUntilIdleAsync();

        var draft = await LatestInvoiceAsync(api, subscription.Id);
        (draft.Status, draft.BillingReason).ShouldBe((InvoiceStatus.Draft, BillingReason.SubscriptionCycle));
        draft.Lines.Select(line => (line.Type, line.Description, line.Amount)).ShouldBe(
        [
            (InvoiceLineType.Subscription, "API (Base) × 1", 10_000L),
            (InvoiceLineType.Usage, "API calls: 1,500", 250L),
            (InvoiceLineType.Item, "Onboarding", 5_000L),
        ]);
        var usage = draft.Lines[1];
        (usage.Quantity, usage.PeriodStart, usage.PeriodEnd).ShouldBe((1_500m, start, second), "an event at the very end of a period belongs to the next one");

        // Reported during the draft period, for the period that ended: it still counts.
        _time.Advance(TimeSpan.FromMinutes(30));
        (await ReportAsync(api, Event("late", customer, "api_calls", 100, second.AddMinutes(-1)))).Accepted.ShouldBe(1);
        _time.Advance(TimeSpan.FromMinutes(30));
        await host.RunWorkersUntilIdleAsync();

        var first = await LatestInvoiceAsync(api, subscription.Id);
        first.Id.ShouldBe(draft.Id);
        first.Status.ShouldBe(InvoiceStatus.Open);
        first.Lines.Select(line => (line.Type, line.Description, line.Amount)).ShouldBe(
        [
            (InvoiceLineType.Subscription, "API (Base) × 1", 10_000L),
            (InvoiceLineType.Usage, "API calls: 1,600", 300L),
            (InvoiceLineType.Item, "Onboarding", 5_000L),
        ]);
        first.Total.ShouldBe(15_300);
        (await BilledUntilAsync(host, subscription.Id)).ShouldBe([second]);

        // Once the invoice is final, its period takes no more events; a retry of a stored event is still a duplicate.
        var afterwards = await ReportAsync(api, Event("too-late", customer, "api_calls", 50, second.AddMinutes(-1)), Event("late", customer, "api_calls", 100, second.AddMinutes(-1)));
        afterwards.Accepted.ShouldBe(0);
        afterwards.Duplicates.ShouldBe(["late"]);
        afterwards.Rejected.Select(rejected => (rejected.Id, rejected.Code)).ShouldBe([("too-late", "usage_event.period_closed")]);

        (await ReportAsync(api, Event("c", customer, "api_calls", 900))).Accepted.ShouldBe(1);
        await RenewAsync(host, third);

        var secondInvoice = await LatestInvoiceAsync(api, subscription.Id);
        secondInvoice.Lines.Select(line => (line.Description, line.Amount, line.PeriodStart)).ShouldBe(
        [
            ("API (Base) × 1", 10_000L, third),
            ("API calls: 1,200", 100L, second),
        ]);

        // A period within the free tier, then one without usage: nothing to bill, and both are closed all the same.
        (await ReportAsync(api, Event("d", customer, "api_calls", 1_000))).Accepted.ShouldBe(1);
        await RenewAsync(host, start.AddMonths(4));

        var last = await LatestInvoiceAsync(api, subscription.Id);
        last.Lines.ShouldHaveSingleItem().Description.ShouldBe("API (Base) × 1");
        (await BilledUntilAsync(host, subscription.Id)).ShouldBe([start.AddMonths(4)]);
    }

    [Fact]
    [Trait("Requirement", "USG-8")]
    [Trait("Requirement", "USG-9")]
    public async Task EachMeter_AggregatesItsWay_AndPricesDecimalQuantities()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var other = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-calls-try", "requests-try", "storage-try", "seats-try", "traffic-try");
        _time.Advance(TimeSpan.FromDays(3));

        (await ReportAsync(
            api,
            Event("s1", customer, "api_calls", 1_000.5m),
            Event("s2", customer, "api_calls", 99.75m),
            Event("s3", other, "api_calls", 5_000),
            Event("r1", customer, "requests", 40),
            Event("r2", customer, "requests"),
            Event("r3", customer, "requests"),
            Event("g1", customer, "storage", 12.5m),
            Event("g2", customer, "storage", 80),
            Event("g3", customer, "storage", 3),
            Event("u1", customer, "seats", properties: new { user_id = "ada" }),
            Event("u2", customer, "seats", properties: new { user_id = "grace" }),
            Event("u3", customer, "seats", properties: new { user_id = "ada" }),
            Event("t1", customer, "traffic", 250.5m))).Accepted.ShouldBe(13);
        await RenewAsync(host, subscription.CurrentPeriodEnd);

        var invoice = await LatestInvoiceAsync(api, subscription.Id);
        invoice.Status.ShouldBe(InvoiceStatus.Open);
        invoice.Lines.Select(line => (line.Description, line.Quantity, line.Amount)).ShouldBe(
        [
            ("API calls: 1,100.25", 1_100.25m, 50L),
            ("Requests: 3", 3m, 30L),
            ("Storage: 80", 80m, 8_000L),
            ("Seats: 2", 2m, 4_000L),
            ("Traffic: 250.5", 250.5m, 3 * 700L),
        ], ignoreOrder: true);
    }

    [Fact]
    [Trait("Requirement", "SUB-29")]
    [Trait("Requirement", "USG-10")]
    public async Task CancellingNow_BillsTheUsageSoFar_UnlessToldNotTo()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var billed = await CustomerAsync(api);
        var unbilled = await CustomerAsync(api);
        var first = await SubscribeAsync(api, billed, "api-base-try", "api-calls-try");
        var second = await SubscribeAsync(api, unbilled, "api-base-try", "api-calls-try");
        _time.Advance(TimeSpan.FromDays(5));
        (await ReportAsync(api, Event("x", billed, "api_calls", 3_000), Event("y", unbilled, "api_calls", 3_000))).Accepted.ShouldBe(2);
        _time.Advance(TimeSpan.FromDays(5));
        var canceledAt = _time.GetUtcNow();

        using var withInvoice = await api.PostJsonAsync($"/api/v1/subscriptions/{first.Id}/cancel", new CancelSubscriptionRequest { At = "now" });
        using var without = await api.PostJsonAsync($"/api/v1/subscriptions/{second.Id}/cancel", new CancelSubscriptionRequest { At = "now", InvoiceNow = false });

        withInvoice.StatusCode.ShouldBe(HttpStatusCode.OK, await withInvoice.Content.ReadAsStringAsync(CancellationToken));
        without.StatusCode.ShouldBe(HttpStatusCode.OK, await without.Content.ReadAsStringAsync(CancellationToken));
        var final = await LatestInvoiceAsync(api, first.Id);
        (final.Status, final.BillingReason, final.Total).ShouldBe((InvoiceStatus.Open, BillingReason.SubscriptionCancel, 1_000L));
        var line = final.Lines.ShouldHaveSingleItem();
        (line.Type, line.Description, line.PeriodStart, line.PeriodEnd).ShouldBe((InvoiceLineType.Usage, "API calls: 3,000", first.CurrentPeriodStart, canceledAt));
        (await LatestInvoiceAsync(api, second.Id)).BillingReason.ShouldBe(BillingReason.SubscriptionCreate, "nothing was invoiced at the cancellation");
        (await BilledUntilAsync(host, first.Id)).ShouldBe([canceledAt]);
        (await BilledUntilAsync(host, second.Id)).ShouldBe([null]);

        var late = await ReportAsync(api, Event("x2", billed, "api_calls", 1, canceledAt.AddDays(-1)), Event("y2", unbilled, "api_calls", 1, canceledAt.AddDays(-1)));
        late.Accepted.ShouldBe(1);
        late.Rejected.ShouldHaveSingleItem().Code.ShouldBe("usage_event.period_closed");
    }

    [Fact]
    [Trait("Requirement", "SUB-12")]
    [Trait("Requirement", "USG-10")]
    public async Task ASubscriptionThatEndsWithItsPeriod_GetsAFinalInvoiceOfItsUsage()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-base-try", "api-calls-try");
        using (var scheduled = await api.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new CancelSubscriptionRequest { At = "period_end" }))
        {
            scheduled.StatusCode.ShouldBe(HttpStatusCode.OK, await scheduled.Content.ReadAsStringAsync(CancellationToken));
        }

        _time.Advance(TimeSpan.FromDays(20));
        (await ReportAsync(api, Event("x", customer, "api_calls", 2_000))).Accepted.ShouldBe(1);
        _time.Advance(TimeSpan.FromDays(20));
        await host.RunWorkersUntilIdleAsync();

        var canceled = await GetSubscriptionAsync(api, subscription.Id);
        (canceled.Status, canceled.EndedAt).ShouldBe((SubscriptionStatus.Canceled, subscription.CurrentPeriodEnd));
        var final = await LatestInvoiceAsync(api, subscription.Id);
        (final.Status, final.BillingReason, final.Total).ShouldBe((InvoiceStatus.Open, BillingReason.SubscriptionCancel, 500L));
        final.Lines.ShouldHaveSingleItem().Description.ShouldBe("API calls: 2,000");
        (await BilledUntilAsync(host, subscription.Id)).ShouldBe([subscription.CurrentPeriodEnd]);
    }

    [Theory]
    [InlineData(ProrationBehavior.AlwaysInvoice)]
    [InlineData(ProrationBehavior.CreateProrations)]
    [Trait("Requirement", "SUB-25")]
    public async Task RemovingAMeteredItem_BillsItsUsageSoFar_AndAddingOneStartsMeteringThen(ProrationBehavior behavior)
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-base-try", "api-calls-try");
        _time.Advance(TimeSpan.FromDays(4));
        (await ReportAsync(api, Event("x", customer, "api_calls", 1_800), Event("early", customer, "requests"))).Accepted.ShouldBe(2);
        _time.Advance(TimeSpan.FromDays(4));
        var changedAt = _time.GetUtcNow();

        using var changed = await api.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new UpdateSubscriptionRequest
        {
            Items = [new SubscriptionItemRequest { Price = "api-base-try" }, new SubscriptionItemRequest { Price = "requests-try" }],
            ProrationBehavior = behavior,
        });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(CancellationToken));

        if (behavior == ProrationBehavior.AlwaysInvoice)
        {
            var invoice = await LatestInvoiceAsync(api, subscription.Id);
            (invoice.Status, invoice.BillingReason, invoice.Total).ShouldBe((InvoiceStatus.Open, BillingReason.SubscriptionUpdate, 400L));
            var line = invoice.Lines.ShouldHaveSingleItem();
            (line.Type, line.Description, line.PeriodStart, line.PeriodEnd).ShouldBe((InvoiceLineType.Usage, "API calls: 1,800", subscription.CurrentPeriodStart, changedAt));
        }

        var closed = await ReportAsync(api, Event("y", customer, "api_calls", 10, changedAt.AddDays(-1)));
        closed.Accepted.ShouldBe(1, "the item is gone, and with it what closed the period: the event is kept, and nobody bills it");
        _time.Advance(TimeSpan.FromDays(2));
        (await ReportAsync(api, Event("r1", customer, "requests"), Event("r2", customer, "requests"))).Accepted.ShouldBe(2);
        await RenewAsync(host, subscription.CurrentPeriodEnd);

        var renewal = await LatestInvoiceAsync(api, subscription.Id);
        renewal.BillingReason.ShouldBe(BillingReason.SubscriptionCycle);
        var expected = new List<(InvoiceLineType, string, long)>
        {
            (InvoiceLineType.Subscription, "API (Base) × 1", 10_000L),
            (InvoiceLineType.Usage, "Requests: 2", 20L),
        };
        if (behavior == ProrationBehavior.CreateProrations)
        {
            expected.Add((InvoiceLineType.Item, "API calls: 1,800", 400L));
        }

        renewal.Lines.Select(line => (line.Type, line.Description, line.Amount)).ShouldBe(expected);
        renewal.Lines[1].PeriodStart.ShouldBe(changedAt, "metering starts when the item joins");
    }

    [Fact]
    [Trait("Requirement", "SUB-25")]
    [Trait("Requirement", "ENG-9")]
    public async Task AMeteredItemThatLeavesAtARenewal_IsBilledOnThatRenewal_AlsoWhenTheWorkerIsBehind()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-base-try", "api-calls-try");
        var (start, second) = (subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd);
        using (var scheduled = await api.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new UpdateSubscriptionRequest
        {
            Items = [new SubscriptionItemRequest { Price = "api-base-try" }, new SubscriptionItemRequest { Price = "requests-try" }],
            Effective = ChangeTiming.PeriodEnd,
        }))
        {
            scheduled.StatusCode.ShouldBe(HttpStatusCode.OK, await scheduled.Content.ReadAsStringAsync(CancellationToken));
        }

        _time.Advance(TimeSpan.FromDays(12));
        (await ReportAsync(api, Event("x", customer, "api_calls", 2_400), Event("before", customer, "requests"))).Accepted.ShouldBe(2);

        // The worker was down for two renewals: each period is still invoiced by itself.
        _time.Advance(start.AddMonths(2).AddDays(-3) - _time.GetUtcNow());
        (await ReportAsync(api, Event("r1", customer, "requests"), Event("r2", customer, "requests"))).Accepted.ShouldBe(2);
        await RenewAsync(host, start.AddMonths(2));

        var invoices = (await GetAsync<Page<InvoiceResponse>>(api, $"/api/v1/invoices?subscription={subscription.Id}")).Items
            .Where(invoice => invoice.BillingReason == BillingReason.SubscriptionCycle).OrderBy(invoice => invoice.PeriodStart).ToList();
        invoices.Select(invoice => (invoice.Status, invoice.PeriodStart)).ShouldBe([(InvoiceStatus.Open, second), (InvoiceStatus.Open, start.AddMonths(2))]);
        invoices[0].Lines.Select(line => (line.Type, line.Description, line.Amount)).ShouldBe(
        [
            (InvoiceLineType.Subscription, "API (Base) × 1", 10_000L),
            (InvoiceLineType.Item, "API calls: 2,400", 700L),
        ]);
        invoices[1].Lines.Select(line => (line.Type, line.Description, line.Amount, line.PeriodStart)).ShouldBe(
        [
            (InvoiceLineType.Subscription, "API (Base) × 1", 10_000L, start.AddMonths(2)),
            (InvoiceLineType.Usage, "Requests: 2", 20L, second),
        ]);
    }

    [Fact]
    [Trait("Requirement", "USG-5")]
    [Trait("Requirement", "USG-12")]
    public async Task AnEventReportedWhileItsPeriodIsInvoiced_WaitsForTheInvoice_AndIsRefused()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var subscription = await SubscribeAsync(api, customer, "api-calls-try");
        _time.Advance(TimeSpan.FromDays(2));
        var closing = _time.GetUtcNow();

        Task<ReportUsageResponse> reporting;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            // What invoicing does: it locks the items, counts, and closes the period.
            var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
            await using var invoicing = await context.Database.BeginTransactionAsync(CancellationToken);
            (await scope.ServiceProvider.GetRequiredService<IUsageStore>().LockItemsAsync(subscription.Id, CancellationToken)).ShouldHaveSingleItem().BilledUntil.ShouldBeNull();
            reporting = ReportAsync(api, Event("racing", customer, "api_calls", 5, closing.AddHours(-1)));
            await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken);
            reporting.IsCompleted.ShouldBeFalse("the report waits for the invoice that is counting");
            await context.Database.ExecuteSqlAsync($"UPDATE tahsil.subscription_items SET usage_billed_until = {closing} WHERE subscription_id = {subscription.Id}", CancellationToken);
            await invoicing.CommitAsync(CancellationToken);
        }

        var answer = await reporting;
        answer.Accepted.ShouldBe(0);
        answer.Rejected.ShouldHaveSingleItem().Code.ShouldBe("usage_event.period_closed");
    }

    [Fact]
    [Trait("Requirement", "USG-7")]
    public async Task ACustomersUsageOnAMeter_IsBilledByOneItem()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var other = await CustomerAsync(api);
        var first = await SubscribeAsync(api, customer, "api-base-try", "api-calls-try");
        await SubscribeAsync(api, other, "api-calls-try");

        using var twice = await api.PostJsonAsync("/api/v1/subscriptions", Request(customer, "api-calls-flat-try"));
        using var together = await api.PostJsonAsync("/api/v1/subscriptions", Request(await CustomerAsync(api), "api-calls-try", "api-calls-flat-try"));
        using var swapped = await api.PatchJsonAsync($"/api/v1/subscriptions/{first.Id}", new UpdateSubscriptionRequest
        {
            Items = [new SubscriptionItemRequest { Price = "api-base-try" }, new SubscriptionItemRequest { Price = "api-calls-flat-try" }],
        });
        using var canceled = await api.PostJsonAsync($"/api/v1/subscriptions/{first.Id}/cancel", new CancelSubscriptionRequest { At = "now" });
        using var afterwards = await api.PostJsonAsync("/api/v1/subscriptions", Request(customer, "api-calls-try"));

        (twice.StatusCode, await twice.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "subscription.meter_conflict"));
        (together.StatusCode, await together.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "subscription.meter_conflict"));
        swapped.StatusCode.ShouldBe(HttpStatusCode.OK, await swapped.Content.ReadAsStringAsync(CancellationToken));
        canceled.StatusCode.ShouldBe(HttpStatusCode.OK, await canceled.Content.ReadAsStringAsync(CancellationToken));
        afterwards.StatusCode.ShouldBe(HttpStatusCode.Created, await afterwards.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    [Trait("Requirement", "USG-10")]
    [Trait("Requirement", "SUB-24")]
    public async Task ATrialsUsageIsFree_AndTheUpcomingInvoiceShowsTheUsageSoFar()
    {
        await using var host = Host();
        using var api = await ApiAsync(host);
        var customer = await CustomerAsync(api);
        var subscription = await CreateAsync(api, Request(customer, "api-base-try", "api-calls-try") with { TrialEnd = _time.GetUtcNow().AddDays(14) });
        _time.Advance(TimeSpan.FromDays(7));
        (await ReportAsync(api, Event("trial", customer, "api_calls", 9_000))).Accepted.ShouldBe(1);
        await RenewAsync(host, subscription.TrialEnd!.Value);

        var firstPaid = await LatestInvoiceAsync(api, subscription.Id);
        firstPaid.Lines.ShouldHaveSingleItem().Description.ShouldBe("API (Base) × 1");

        _time.Advance(TimeSpan.FromDays(3));
        (await ReportAsync(api, Event("paid", customer, "api_calls", 1_400))).Accepted.ShouldBe(1);
        var upcoming = await GetAsync<InvoicePreviewResponse>(api, $"/api/v1/invoices/upcoming?subscription={subscription.Id}");

        upcoming.Lines.Select(line => (line.Type, line.Description, line.Amount)).ShouldBe(
        [
            (InvoiceLineType.Subscription, "API (Base) × 1", 10_000L),
            (InvoiceLineType.Usage, "API calls: 1,400", 200L),
        ]);
    }

    private static async Task<InvoiceResponse> LatestInvoiceAsync(HttpClient api, Guid subscription) =>
        await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{(await GetSubscriptionAsync(api, subscription)).LatestInvoice!.Id}");

    private static Task<List<DateTimeOffset?>> BilledUntilAsync(WebApplicationFactory<Program> host, Guid subscription) =>
        host.QueryAsync<DateTimeOffset?>($"SELECT usage_billed_until AS \"Value\" FROM tahsil.subscription_items WHERE subscription_id = {subscription} AND quantity IS NULL");

    /// <summary>Moves to <paramref name="at"/>, where the workers bill what is due, and past the draft period, where they finalize it.</summary>
    private async Task RenewAsync(WebApplicationFactory<Program> host, DateTimeOffset at)
    {
        _time.Advance(at - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await host.RunWorkersUntilIdleAsync();
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("usage_billing"), _time);
}
