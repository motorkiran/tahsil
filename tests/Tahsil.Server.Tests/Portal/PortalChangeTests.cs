using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using ConsentType = Tahsil.Domain.Customers.ConsentType;

namespace Tahsil.Server.Tests.Portal;

/// <summary>
/// Plan changes and card updates in the portal (PRT §3, §6), with the simulator: a monthly Team subscription from
/// April 1, 2026 is changed on April 16, halfway through its 30 days, among the Team product's prices.
/// </summary>
public sealed class PortalChangeTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PRT-5")]
    [Trait("Requirement", "PRT-6")]
    [Trait("Requirement", "PRT-7")]
    public async Task AnUpgrade_IsPreviewed_ChargedNow_AndRecordedWithItsButton()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (customer, subscription, _) = await SubscriberAsync(api, "ada@example.com");
        _time.Advance(TimeSpan.FromDays(15));
        using var browser = Open(host);
        var plan = await PlanUrlAsync(api, customer, subscription.Id);
        var plus = (await GetAsync<PriceResponse>(api, "/api/v1/prices/team-plus-monthly-try")).Id;

        var choices = await ParseAsync(await browser.GetAsync(plan, CancellationToken));
        var preview = await ParseAsync(await PostHandlerAsync(browser, plan, "Preview", ("Price", plus.ToString()), ("Quantity", "1")));
        using var confirmed = await PostHandlerAsync(browser, plan, "Confirm", ("Price", plus.ToString()), ("Quantity", "1"));

        choices.QuerySelectorAll("#offers .offer").Select(offer => offer.TextContent.Trim().ReplaceLineEndings(" ")).ShouldBe(
            ["Team (Aylık): ₺300.00 (monthly) Current plan", "Team (Yıllık): ₺3,000.00 (yearly)", "Team (Plus Aylık): ₺600.00 (monthly)"], ignoreOrder: true);
        choices.QuerySelector("#quantity").ShouldBeNull("quantities are not offered unless configured");
        (preview.QuerySelector("#effective")!.TextContent, preview.QuerySelector("#due-now")!.TextContent, preview.QuerySelector("#next-renewal")!.TextContent).ShouldBe(("Now", "₺150.00", "₺600.00"));
        preview.QuerySelector("#confirm")!.TextContent.ShouldBe("Pay ₺150.00 and upgrade");
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        confirmed.Headers.Location!.OriginalString.ShouldEndWith("?done=plan");
        var changed = await GetSubscriptionAsync(api, subscription.Id);
        changed.Items.ShouldHaveSingleItem().Price.Key.ShouldBe("team-plus-monthly-try");
        var invoice = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{changed.LatestInvoice!.Id}");
        (invoice.Status, invoice.Total, invoice.BillingReason).ShouldBe((InvoiceStatus.Paid, 15_000L, BillingReason.SubscriptionUpdate));
        var consent = await QueryAsync(host, context => context.ConsentRecords.SingleAsync(record => record.Type == ConsentType.PlanChange, CancellationToken));
        (consent.TextShown, consent.SubscriptionId, consent.Context["dueNow"], consent.Context["plan"]).ShouldBe(("Pay ₺150.00 and upgrade", subscription.Id, "15000", "Team (Plus Aylık)"));
        (await host.QueryAsync<string>($"SELECT actor_type AS \"Value\" FROM tahsil.audit_events WHERE action = 'subscription.updated' AND subject_id = {subscription.Id.ToString()}"))
            .ShouldContain("customer");
    }

    [Fact]
    [Trait("Requirement", "PRT-6")]
    public async Task ADowngrade_WaitsForThePeriodsEnd_AndAnotherQuantityIsAnUpgrade()
    {
        await using var host = Host(new() { ["Tahsil:Portal:QuantityLimits:Maximum"] = "5" });
        using var api = await ClientAsync(host);
        var (customer, subscription, _) = await SubscriberAsync(api, "ada@example.com", "team-plus-monthly-try");
        _time.Advance(TimeSpan.FromDays(15));
        using var browser = Open(host);
        var plan = await PlanUrlAsync(api, customer, subscription.Id);
        var monthly = (await GetAsync<PriceResponse>(api, "/api/v1/prices/team-monthly-try")).Id;
        var plus = (await GetAsync<PriceResponse>(api, "/api/v1/prices/team-plus-monthly-try")).Id;

        var preview = await ParseAsync(await PostHandlerAsync(browser, plan, "Preview", ("Price", monthly.ToString()), ("Quantity", "1")));
        using var downgraded = await PostHandlerAsync(browser, plan, "Confirm", ("Price", monthly.ToString()), ("Quantity", "1"));
        var scheduled = await GetSubscriptionAsync(api, subscription.Id);
        var seats = await ParseAsync(await PostHandlerAsync(browser, plan, "Preview", ("Price", plus.ToString()), ("Quantity", "3")));
        using var tooMany = await PostHandlerAsync(browser, plan, "Preview", ("Price", plus.ToString()), ("Quantity", "6"));
        using var same = await PostHandlerAsync(browser, plan, "Preview", ("Price", plus.ToString()), ("Quantity", "1"));

        (preview.QuerySelector("#effective")!.TextContent, preview.QuerySelector("#due-now")!.TextContent).ShouldBe(("May 1, 2026", "₺0.00"));
        preview.QuerySelector("#confirm")!.TextContent.ShouldBe("Change plan on May 1, 2026");
        downgraded.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        scheduled.Items.Single().Price.Key.ShouldBe("team-plus-monthly-try");
        (scheduled.ScheduledChange!.EffectiveAt, scheduled.ScheduledChange.Items.Single().Price.Key).ShouldBe((April.AddMonths(1), "team-monthly-try"));
        seats.QuerySelector("#confirm")!.TextContent.ShouldBe("Pay ₺600.00 and upgrade", "two more seats for the half of the period that is left");
        (tooMany.StatusCode, (await ParseAsync(tooMany)).QuerySelector("#portal-error")!.TextContent).ShouldBe((HttpStatusCode.BadRequest, "This plan or quantity is not offered."));
        (same.StatusCode, (await ParseAsync(same)).QuerySelector("#portal-error")!.TextContent).ShouldBe((HttpStatusCode.BadRequest, "This is your current plan."));
    }

    [Fact]
    [Trait("Requirement", "PRT-2")]
    [Trait("Requirement", "PRT-3")]
    [Trait("Requirement", "PRT-6")]
    public async Task AnUpgradeTheCardCannotPay_IsPaidOnTheInvoicesPage_AndOnlyOnesOwnOfferedPlansCanBeChosen()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (ada, subscription, card) = await SubscriberAsync(api, "ada@example.com");
        var (_, hers, _) = await SubscriberAsync(api, "grace@example.com");
        await SetBehaviorAsync(api, card, "insufficient_funds");
        _time.Advance(TimeSpan.FromDays(15));
        using var browser = Open(host);
        var session = await SessionAsync(api, new CreatePortalSessionRequest
        {
            Customer = ada,
            ReturnUrl = "https://app.example.com/billing",
            Locale = "en",
            Flow = PortalFlow.SubscriptionUpdate,
            FlowSubscription = subscription.Id,
        });
        var plan = new Uri($"{session.Url}/subscriptions/{subscription.Id}/change");
        var plus = (await GetAsync<PriceResponse>(api, "/api/v1/prices/team-plus-monthly-try")).Id;
        var starter = (await GetAsync<PriceResponse>(api, "/api/v1/prices/starter-monthly-try")).Id;

        using var start = await browser.GetAsync(session.Url, CancellationToken);
        using var theirs = await browser.GetAsync(new Uri($"{session.Url}/subscriptions/{hers.Id}/change"), CancellationToken);
        using var theirsPosted = await PostHandlerAsync(browser, plan, "Confirm", ("Price", plus.ToString()), ("Quantity", "1"), ("id", hers.Id.ToString()));
        using var notOffered = await PostHandlerAsync(browser, plan, "Preview", ("Price", starter.ToString()), ("Quantity", "1"));
        using var confirmed = await PostHandlerAsync(browser, plan, "Confirm", ("Price", plus.ToString()), ("Quantity", "1"));

        (start.StatusCode, start.Headers.Location?.OriginalString).ShouldBe((HttpStatusCode.Redirect, plan.AbsolutePath));
        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        notOffered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        theirsPosted.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a posted ID does not reach another customer's subscription");
        (await GetSubscriptionAsync(api, hers.Id)).Items.Single().Price.Key.ShouldBe("team-monthly-try");
        var changed = await GetSubscriptionAsync(api, subscription.Id);
        var invoice = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{changed.LatestInvoice!.Id}");
        (changed.Items.Single().Price.Key, invoice.Status, invoice.AmountDue).ShouldBe(("team-plus-monthly-try", InvoiceStatus.Open, 15_000L));
        (confirmed.StatusCode, confirmed.Headers.Location).ShouldBe((HttpStatusCode.Redirect, invoice.HostedInvoiceUrl));
    }

    [Fact]
    [Trait("Requirement", "PRT-11")]
    [Trait("Requirement", "CUS-13")]
    [Trait("Requirement", "DUN-8")]
    public async Task ANewCard_BecomesTheDefault_ForTheSubscriptionsOfTheOldOne_AndPaysWhatWaited()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (customer, subscription, card) = await SubscriberAsync(api, "ada@example.com", ownCard: true);
        await SetBehaviorAsync(api, card, "expired_card");
        foreach (var instant in new[] { April.AddMonths(1), April.AddMonths(1).AddHours(1) })
        {
            _time.Advance(instant - _time.GetUtcNow());
            await host.RunWorkersUntilIdleAsync();
        }

        var pastDue = await GetSubscriptionAsync(api, subscription.Id);
        using var browser = Open(host);
        var session = await SessionAsync(api, new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "en", Flow = PortalFlow.PaymentMethodUpdate });
        var cards = new Uri($"{session.Url}/payment-method");

        using var start = await browser.GetAsync(session.Url, CancellationToken);
        using var withoutConsent = await PostHandlerAsync(browser, cards, "Add");
        using var returned = await PayOnTheEmbedAsync(host, browser, await PostHandlerAsync(browser, cards, "Add", ("CardStorage", "true")), "4000000000007726");
        var next = (await ParseAsync(returned)).QuerySelector("#continue")!.GetAttribute("href");
        await host.RunWorkersUntilIdleAsync();

        (start.StatusCode, start.Headers.Location?.OriginalString).ShouldBe((HttpStatusCode.Redirect, cards.AbsolutePath));
        pastDue.Status.ShouldBe(SubscriptionStatus.PastDue);
        withoutConsent.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        next.ShouldBe("https://app.example.com/billing", "a session opened to update the card goes back to the application");
        var saved = (await GetAsync<Page<PaymentMethodResponse>>(api, $"/api/v1/customers/{customer}/payment-methods")).Items.Single(method => method.Last4 == "7726");
        var recovered = await GetSubscriptionAsync(api, subscription.Id);
        (saved.IsDefault, recovered.DefaultPaymentMethodId, recovered.Status).ShouldBe((true, saved.Id, SubscriptionStatus.Active));
        (await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{recovered.LatestInvoice!.Id}")).Status.ShouldBe(InvoiceStatus.Paid);
        var consent = await QueryAsync(host, context => context.ConsentRecords.SingleAsync(record => record.PortalSessionId == session.Id, CancellationToken));
        (consent.Type, consent.TextShown).ShouldBe((ConsentType.CardStorage, "I agree that Acme saves my card and charges it for my future payments. I can remove the saved card at any time."));
    }

    [Fact]
    [Trait("Requirement", "PRT-3")]
    [Trait("Requirement", "PRT-11")]
    public async Task OnesOwnCards_AreMadeDefaultOrRemoved_AndWithoutASetupTheCardChangesAtAPayment()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (customer, _, first) = await SubscriberAsync(api, "ada@example.com");
        var second = await SaveCardAsync(api, customer, "4000000000007726");
        var (_, _, hers) = await SubscriberAsync(api, "grace@example.com");
        using var browser = Open(host);
        var session = await SessionAsync(api, new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "en" });
        var cards = new Uri($"{session.Url}/payment-method");

        using var theirs = await PostHandlerAsync(browser, cards, "Default", ("Card", hers.ToString()));
        using var theirsRemoved = await PostHandlerAsync(browser, cards, "Remove", ("Card", hers.ToString()));
        using var madeDefault = await PostHandlerAsync(browser, cards, "Default", ("Card", second.ToString()));
        using var defaultRemoved = await PostHandlerAsync(browser, cards, "Remove", ("Card", second.ToString()));
        using var removed = await PostHandlerAsync(browser, cards, "Remove", ("Card", first.ToString()));
        var left = (await GetAsync<Page<PaymentMethodResponse>>(api, $"/api/v1/customers/{customer}/payment-methods?status=active")).Items;
        using (var updated = await api.PatchJsonAsync("/api/v1/provider-connections/simulator", new { settings = new Dictionary<string, string> { ["setup"] = "none" } }))
        {
            updated.EnsureSuccessStatusCode();
        }

        var page = await ParseAsync(await browser.GetAsync(cards, CancellationToken));

        (theirs.StatusCode, theirsRemoved.StatusCode).ShouldBe((HttpStatusCode.NotFound, HttpStatusCode.NotFound));
        (madeDefault.StatusCode, madeDefault.Headers.Location!.OriginalString).ShouldBe((HttpStatusCode.Redirect, $"{session.Url.AbsolutePath}?done=default"));
        defaultRemoved.Headers.Location!.OriginalString.ShouldEndWith("?failed=portal.card_is_default");
        removed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        left.ShouldHaveSingleItem().ShouldSatisfyAllConditions(card => card.Id.ShouldBe(second), card => card.IsDefault.ShouldBeTrue());
        (await GetAsync<Page<PaymentMethodResponse>>(api, $"/api/v1/customers/{await CustomerOfAsync(host, hers)}/payment-methods?status=active")).Items.ShouldHaveSingleItem();
        page.QuerySelector("#add-card-form").ShouldBeNull();
        page.QuerySelector("#next-payment")!.TextContent.ShouldBe("Your card is updated when you make your next payment.");
    }

    /// <summary>A customer with a monthly subscription paid by a card, which is the subscription's own when asked.</summary>
    private static async Task<(Guid Customer, SubscriptionResponse Subscription, Guid Card)> SubscriberAsync(
        HttpClient api, string email, string price = "team-monthly-try", bool ownCard = false)
    {
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada Lovelace", Email = email, Locale = "en", Type = CustomerType.Company });
        var customer = (await created.ReadAsync<CustomerResponse>()).Id;
        var card = await SaveCardAsync(api, customer, "4242424242424242");
        var subscription = await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = price }], DefaultPaymentMethod = ownCard ? card : null });
        return (customer, subscription, card);
    }

    private static async Task<Uri> PlanUrlAsync(HttpClient api, Guid customer, Guid subscription)
    {
        var session = await SessionAsync(api, new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "en" });
        return new Uri($"{session.Url}/subscriptions/{subscription}/change");
    }

    private static async Task<PortalSessionResponse> SessionAsync(HttpClient api, CreatePortalSessionRequest request)
    {
        using var response = await api.PostJsonAsync("/api/v1/portal-sessions", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<PortalSessionResponse>();
    }

    private static Task<Guid> CustomerOfAsync(WebApplicationFactory<Program> host, Guid card) =>
        QueryAsync(host, context => context.PaymentMethods.Where(method => method.Id == card).Select(method => method.CustomerId).SingleAsync(CancellationToken));

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["Tahsil:Portal:Products:0:Product"] = "team";
        return IsolatedHosts.Create(server, server.NewDatabase("portal_changes"), _time, settings: settings);
    }
}
