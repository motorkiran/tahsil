using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using DomainCancellationReason = Tahsil.Domain.Subscriptions.CancellationReason;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>The subscription worker (ENG-8, ENG-9): renewals, trial ends, cancellations and expiries, in order and on time.</summary>
public sealed class SubscriptionCycleTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    [Fact]
    [Trait("Requirement", "ENG-9")]
    [Trait("Requirement", "SUB-11")]
    public async Task ASubscriptionThreePeriodsBehind_GetsThreeCycleInvoices_EachForItsPeriod()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client));
        var start = subscription.CurrentPeriodStart;

        _time.Advance(TimeSpan.FromDays(95));
        await host.RunWorkersUntilIdleAsync();

        (await CycleInvoicesAsync(host)).ShouldBe([Period(start, 1, 2), Period(start, 2, 3), Period(start, 3, 4)]);
        var renewed = await GetSubscriptionAsync(client, subscription.Id);
        (renewed.Status, renewed.CurrentPeriodStart, renewed.CurrentPeriodEnd).ShouldBe((SubscriptionStatus.Active, start.AddMonths(3), start.AddMonths(4)));
        (await host.QueryAsync<long>($"SELECT count(*) AS \"Value\" FROM tahsil.audit_events WHERE action = 'subscription.renewed'")).ShouldBe([3L]);
    }

    [Fact]
    [Trait("Requirement", "INV-5")]
    public async Task ACycleInvoice_IsADraft_ThatFinalizesItselfAfterTheDraftPeriod()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client), quantity: 3);

        _time.Advance(subscription.CurrentPeriodEnd - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();

        var renewed = await GetSubscriptionAsync(client, subscription.Id);
        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{renewed.LatestInvoice!.Id}");
        (invoice.Status, invoice.BillingReason, invoice.AutoAdvance, invoice.FinalizeAt, invoice.Total).ShouldBe(
            (InvoiceStatus.Draft, BillingReason.SubscriptionCycle, true, _time.GetUtcNow().AddHours(1), 90_000L));
        invoice.Lines.ShouldHaveSingleItem().Description.ShouldBe("Team (Aylık) × 3");
    }

    [Fact]
    [Trait("Requirement", "SUB-12")]
    [Trait("Requirement", "SUB-28")]
    public async Task ACancellationAtThePeriodEnd_EndsIt_WithoutANewPeriod()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client));
        var end = subscription.CurrentPeriodEnd;
        await ChangeAsync(host, subscription.Id, model => model.ScheduleCancellation(end, DomainCancellationReason.RequestedByCustomer, null, null, _time.GetUtcNow()));

        _time.Advance(TimeSpan.FromDays(40));
        await host.RunWorkersUntilIdleAsync();

        var canceled = await GetSubscriptionAsync(client, subscription.Id);
        (canceled.Status, canceled.EndedAt, canceled.CancellationReason, canceled.CurrentPeriodEnd).ShouldBe((SubscriptionStatus.Canceled, end, CancellationReason.RequestedByCustomer, end));
        (await CycleInvoicesAsync(host)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "SUB-12")]
    public async Task ATrialCanceledAtItsEnd_EndsWithoutAPaidPeriod()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var trial = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });
        await ChangeAsync(host, trial.Id, model => model.ScheduleCancellation(trial.TrialEnd!.Value, DomainCancellationReason.RequestedByCustomer, null, null, _time.GetUtcNow()));

        _time.Advance(TimeSpan.FromDays(15));
        await host.RunWorkersUntilIdleAsync();

        var canceled = await GetSubscriptionAsync(client, trial.Id);
        (canceled.Status, canceled.EndedAt).ShouldBe((SubscriptionStatus.Canceled, trial.TrialEnd));
        (await CycleInvoicesAsync(host)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "SUB-14")]
    public async Task ATrialWithACard_StartsItsFirstPaidPeriodAtItsEnd()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var trial = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });
        var trialEnd = trial.TrialEnd!.Value;

        _time.Advance(TimeSpan.FromDays(14));
        await host.RunWorkersUntilIdleAsync();

        var converted = await GetSubscriptionAsync(client, trial.Id);
        (converted.CurrentPeriodStart, converted.CurrentPeriodEnd).ShouldBe((trialEnd, trialEnd.AddMonths(1)));
        (await CycleInvoicesAsync(host)).ShouldBe([(trialEnd, trialEnd.AddMonths(1))]);
    }

    [Theory]
    [Trait("Requirement", "SUB-15")]
    [InlineData(TrialEndBehavior.Cancel, SubscriptionStatus.Canceled, 0)]
    [InlineData(TrialEndBehavior.CreateInvoice, SubscriptionStatus.Trialing, 1)]
    public async Task ATrialWithoutACard_IsCanceled_OrInvoicedWhenAgreed(TrialEndBehavior behavior, SubscriptionStatus status, int invoices)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var trial = await CreateAsync(client, new CreateSubscriptionRequest
        {
            Customer = await CustomerAsync(client),
            Items = [new() { Price = "pro-monthly-try" }],
            TrialEndBehavior = behavior,
        });

        _time.Advance(TimeSpan.FromDays(14));
        await host.RunWorkersUntilIdleAsync();

        var ended = await GetSubscriptionAsync(client, trial.Id);
        ended.Status.ShouldBe(status);
        (await CycleInvoicesAsync(host)).Count.ShouldBe(invoices);
        if (status == SubscriptionStatus.Canceled)
        {
            (ended.CancellationReason, ended.EndedAt).ShouldBe((CancellationReason.TrialWithoutPaymentMethod, trial.TrialEnd));
        }
    }

    [Fact]
    [Trait("Requirement", "SUB-8")]
    public async Task AnIncompleteSubscription_ExpiresAfterADay_WithItsInvoiceVoided()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242", behavior: "card_declined");
        var incomplete = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });

        _time.Advance(TimeSpan.FromHours(23));
        await host.RunWorkersUntilIdleAsync();

        var expired = await GetSubscriptionAsync(client, incomplete.Id);
        (expired.Status, expired.EndedAt, expired.CancellationReason, expired.LatestInvoice!.Status).ShouldBe(
            (SubscriptionStatus.IncompleteExpired, incomplete.IncompleteExpiresAt, CancellationReason.IncompleteExpired, InvoiceStatus.Void));
    }

    [Fact]
    [Trait("Requirement", "DOM-13")]
    [Trait("Requirement", "ENG-3")]
    public async Task TwoInstancesRenewingAtOnce_BillEachPeriodOnce()
    {
        var database = server.NewDatabase("cycles");
        await using var first = IsolatedHosts.Create(server, database, _time);
        await using var second = IsolatedHosts.Create(server, database, _time);
        using var client = await ClientAsync(first);
        var customer = await CustomerAsync(client);
        for (var i = 0; i < 5; i++)
        {
            await SendingInvoicesAsync(client, customer);
        }

        _time.Advance(TimeSpan.FromDays(95));
        await Task.WhenAll(first.RunWorkersUntilIdleAsync(), second.RunWorkersUntilIdleAsync());

        (await first.QueryAsync<long>($"""
            SELECT count(DISTINCT (subscription_id, period_start)) AS "Value" FROM tahsil.invoices WHERE billing_reason = 'subscription_cycle'
            """)).ShouldBe([15L]);
        (await CycleInvoicesAsync(first)).Count.ShouldBe(15);
    }

    [Fact]
    [Trait("Requirement", "INV-1")]
    [Trait("Requirement", "INV-5")]
    [Trait("Requirement", "ENG-11")]
    public async Task ItemsForASubscription_JoinItsCycleDraft_UntilItIsFinalized_ThenTheNextInvoice()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        var subscription = await SendingInvoicesAsync(client, customer);
        _time.Advance(subscription.CurrentPeriodEnd - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
        var draft = (await GetSubscriptionAsync(client, subscription.Id)).LatestInvoice!.Id;

        var joined = await AddItemAsync(client, customer, subscription.Id, "Extra storage");
        _time.Advance(TimeSpan.FromHours(1));
        await host.RunWorkersUntilIdleAsync();
        var waiting = await AddItemAsync(client, customer, subscription.Id, "Onboarding call");

        var finalized = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{draft}");
        (joined.InvoiceId, waiting.InvoiceId).ShouldBe((draft, null));
        (finalized.Status, finalized.Total).ShouldBe((InvoiceStatus.Open, 35_000L));
        finalized.Lines.Select(line => line.Type).ShouldBe([InvoiceLineType.Subscription, InvoiceLineType.Item]);

        _time.Advance(TimeSpan.FromDays(32));
        await host.RunWorkersUntilIdleAsync();
        var next = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{(await GetSubscriptionAsync(client, subscription.Id)).LatestInvoice!.Id}");
        next.Lines.Select(line => line.Description).ShouldBe(["Team (Aylık) × 1", "Onboarding call"]);
    }

    [Fact]
    [Trait("Requirement", "SUB-11")]
    [Trait("Requirement", "INV-10")]
    [Trait("Requirement", "ENG-13")]
    public async Task ARenewalIsChargedToTheSubscriptionsCard_AndADeclineMakesItPastDue_UntilPaid()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var card = await SaveCardAsync(client, customer, "4000000000007726");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }], DefaultPaymentMethod = card });
        await SetBehaviorAsync(client, card, "card_declined");

        _time.Advance(subscription.CurrentPeriodEnd - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await host.RunWorkersUntilIdleAsync();

        var pastDue = await GetSubscriptionAsync(client, subscription.Id);
        var renewal = pastDue.LatestInvoice!.Id;
        var payment = (await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={renewal}")).Items.ShouldHaveSingleItem();
        (pastDue.Status, pastDue.PastDueSince).ShouldBe((SubscriptionStatus.PastDue, payment.CreatedAt));
        (payment.PaymentMethodId, payment.Status).ShouldBe((card, PaymentStatus.Failed));

        await SetBehaviorAsync(client, card, "succeed");
        using (var paid = await client.PostJsonAsync($"/api/v1/invoices/{renewal}/pay", new PayInvoiceRequest { PaymentMethod = card }))
        {
            paid.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        }

        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Active);
    }

    private static (DateTimeOffset, DateTimeOffset) Period(DateTimeOffset start, int from, int to) => (start.AddMonths(from), start.AddMonths(to));

    private static async Task<List<(DateTimeOffset, DateTimeOffset)>> CycleInvoicesAsync(WebApplicationFactory<Program> host) =>
        [.. (await host.QueryAsync<CyclePeriod>($"""
            SELECT period_start AS start, period_end AS "end" FROM tahsil.invoices WHERE billing_reason = 'subscription_cycle' ORDER BY period_start
            """)).Select(period => (period.Start, period.End))];

    private static async Task<InvoiceItemResponse> AddItemAsync(HttpClient client, Guid customer, Guid subscription, string description)
    {
        using var response = await client.PostJsonAsync("/api/v1/invoice-items", new CreateInvoiceItemRequest
        {
            Customer = customer,
            Subscription = subscription,
            Currency = "TRY",
            Description = description,
            UnitAmount = 5_000,
        });
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await response.ReadAsync<InvoiceItemResponse>();
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("cycles"), _time);

    private sealed record CyclePeriod(DateTimeOffset Start, DateTimeOffset End);
}
