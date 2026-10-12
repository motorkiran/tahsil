using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Notifications;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Server.Tests.Notifications;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using CheckoutStatus = Tahsil.Domain.Checkout.CheckoutStatus;
using ConsentType = Tahsil.Domain.Customers.ConsentType;
using PaymentPurpose = Tahsil.Domain.Payments.PaymentPurpose;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>
/// Subscription sessions (CHK §9) with the simulator, from October 1, 2026 at 09:00 UTC: monthly Team at ₺300.00 renews
/// on November 1, and Pro's 14-day trial ends on October 15.
/// </summary>
public sealed class SubscriptionCheckoutTests(TahsilServerFixture server)
{
    private static readonly string[] Documents = ["pre-information", "distance-sales"];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);
    private readonly CapturingEmailSender _email = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CHK-1")]
    [Trait("Requirement", "CHK-8")]
    [Trait("Requirement", "CHK-13")]
    [Trait("Requirement", "CHK-17")]
    [Trait("Requirement", "CHK-23")]
    public async Task ASubscription_IsBought_WithItsFirstInvoicePaid_AndTheCardSavedWithItsConsent()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var session = await SessionAsync(api, ["team-monthly-try", "setup-fee-try"]);
        using var browser = Open(host);

        var page = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        using var withoutCardConsent = await SubmitAsync(browser, session.Url!, Form("ada@example.com", "Ada Lovelace", Documents));
        using var returned = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, WithCardConsent()), "4242424242424242");

        (session.Quote.AmountDue, session.Quote.Recurring!.Period.Total, session.Subscription!.CollectionMethod).ShouldBe((40_000L, 30_000L, CollectionMethod.ChargeAutomatically));
        page.QuerySelector("#pay")!.TextContent.Trim().ShouldBe("Pay ₺400.00 and subscribe");
        page.QuerySelector("#renewal")!.TextContent.ShouldBe("Then ₺300.00 monthly from November 1, 2026, until you cancel.");
        page.QuerySelector("#card-storage")!.ParentElement!.TextContent.Trim().ShouldBe(
            "I agree that my card is saved and charged ₺300.00 monthly for my Team subscription. The first automatic charge is on November 1, 2026. I can cancel my subscription at any time.");
        withoutCardConsent.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var subscription = await GetSubscriptionAsync(api, stored.SubscriptionId!.Value);
        var invoice = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{stored.InvoiceId}");
        (stored.Status, subscription.Status, subscription.DefaultPaymentMethodId, subscription.CheckoutSessionId).ShouldBe(
            (CheckoutStatus.Complete, Contracts.SubscriptionStatus.Active, stored.PaymentMethodId, (Guid?)session.Id));
        stored.PaymentMethodId.ShouldNotBeNull();
        (invoice.Status, invoice.BillingReason, invoice.Total, invoice.SubscriptionId).ShouldBe((Contracts.InvoiceStatus.Paid, BillingReason.SubscriptionCreate, 40_000L, (Guid?)subscription.Id));
        invoice.Lines.Select(line => line.Amount).ShouldBe([30_000L, 10_000L]);
        subscription.CurrentPeriodEnd.ShouldBe(FakeBillingClock.Start.AddMonths(1));
        var method = await QueryAsync(host, context => context.PaymentMethods.SingleAsync(row => row.Id == stored.PaymentMethodId, CancellationToken));
        var consent = await QueryAsync(host, context => context.ConsentRecords.SingleAsync(record => record.Id == method.ConsentRecordId, CancellationToken));
        (consent.Type, consent.TextShown).ShouldBe((ConsentType.CardStorage, page.QuerySelector("#card-storage")!.ParentElement!.TextContent.Trim()));
    }

    [Fact]
    [Trait("Requirement", "CHK-14")]
    [Trait("Requirement", "CHK-25")]
    [Trait("Requirement", "SUB-15")]
    public async Task ATrialWithoutACard_StartsWhenTheFormIsSubmitted_AndEndsWithoutOne()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var session = await SessionAsync(api, ["pro-monthly-try"]);
        using var browser = Open(host);

        var page = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        using var submitted = await SubmitAsync(browser, session.Url!, Form("ada@example.com", "Ada Lovelace", Documents));
        await host.RunWorkersUntilIdleAsync();
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var trialing = await GetSubscriptionAsync(api, stored.SubscriptionId!.Value);
        _time.Advance(TimeSpan.FromDays(15));
        await host.RunWorkersUntilIdleAsync();

        page.QuerySelector("#pay")!.TextContent.Trim().ShouldBe("Start the trial");
        page.QuerySelector("#trial")!.TextContent.ShouldBe("Your trial ends on October 15, 2026. Add a card before then to continue.");
        page.QuerySelector("#total-due")!.TextContent.ShouldBe("₺0.00");
        page.QuerySelector("#card-storage").ShouldBeNull();
        submitted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        submitted.Headers.Location!.ShouldBe(new Uri("https://shop.example.com/done"));
        (stored.Status, stored.PaymentId, stored.InvoiceId).ShouldBe((CheckoutStatus.Complete, null, null));
        (trialing.Status, trialing.TrialEnd, trialing.DefaultPaymentMethodId).ShouldBe((Contracts.SubscriptionStatus.Trialing, FakeBillingClock.Start.AddDays(14), null));
        (await QueryAsync(host, context => context.Payments.CountAsync(CancellationToken))).ShouldBe(0);
        (await GetSubscriptionAsync(api, trialing.Id)).Status.ShouldBe(Contracts.SubscriptionStatus.Canceled);
        var confirmation = _email.Sent.Single(message => message.Subject == "Your subscription is confirmed");
        confirmation.Text.ShouldContain("Your Pro trial started on October 1, 2026 and ends on October 15, 2026. Nothing was charged.");
        confirmation.Text.ShouldContain("Add a card before then to continue");
    }

    [Fact]
    [Trait("Requirement", "CHK-13")]
    [Trait("Requirement", "CHK-14")]
    public async Task ATrialWithACard_SavesItWithoutCharging()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var session = await SessionAsync(api, ["pro-monthly-try"], new CheckoutSubscriptionData { PaymentMethodCollection = PaymentMethodCollectionMode.Always });
        using var browser = Open(host);

        var page = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        using var returned = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, WithCardConsent()), "4242424242424242");

        page.QuerySelector("#pay")!.TextContent.Trim().ShouldBe("Save my card and start the trial");
        page.QuerySelector("#trial")!.TextContent.Trim().ShouldBe("Your trial ends on October 15, 2026.");
        page.QuerySelector("#card-storage")!.ParentElement!.TextContent.ShouldContain("The first automatic charge is on October 15, 2026.");
        page.QuerySelector("#summary")!.TextContent.ShouldNotContain("free", Case.Insensitive);
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var payment = await QueryAsync(host, context => context.Payments.SingleAsync(row => row.Id == stored.PaymentId, CancellationToken));
        var subscription = await GetSubscriptionAsync(api, stored.SubscriptionId!.Value);
        (payment.Purpose, payment.Amount).ShouldBe((PaymentPurpose.Setup, 0L));
        (subscription.Status, subscription.DefaultPaymentMethodId).ShouldBe((Contracts.SubscriptionStatus.Trialing, stored.PaymentMethodId));
        stored.PaymentMethodId.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Requirement", "CHK-24")]
    public async Task ACardTheCustomerDidNotSave_StillMakesTheSubscription_AndTheEmailAsksForOne()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await SimulatorSavesCardsAsync(api, "during_payment_when_customer_agrees");
        var session = await SessionAsync(api, ["team-monthly-try"]);
        using var browser = Open(host);

        using var returned = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, WithCardConsent()), "4242424242424242", saveCard: false);
        await host.RunWorkersUntilIdleAsync();

        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var subscription = await GetSubscriptionAsync(api, stored.SubscriptionId!.Value);
        (stored.Status, stored.PaymentMethodId, subscription.Status, subscription.DefaultPaymentMethodId).ShouldBe((CheckoutStatus.Complete, null, Contracts.SubscriptionStatus.Active, null));
        _email.Sent.Single(message => message.Subject.StartsWith("Your subscription is confirmed", StringComparison.Ordinal))
            .Text.ShouldContain("Your card was not saved. Add a card before November 1, 2026 so that your subscription renews.");
    }

    [Fact]
    [Trait("Requirement", "CHK-3")]
    public async Task AConnectionThatSavesNoCards_InvoicesTheSubscription_AndRefusesTrialsWithACard()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await SimulatorSavesCardsAsync(api, "none");

        var session = await SessionAsync(api, ["team-monthly-try"]);
        using var cardTrial = await api.PostJsonAsync("/api/v1/checkout-sessions", Request(["pro-monthly-try"], new CheckoutSubscriptionData { PaymentMethodCollection = PaymentMethodCollectionMode.Always }));
        using var browser = Open(host);
        var page = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        using var returned = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, Form("ada@example.com", "Ada Lovelace", Documents)), "4242424242424242");

        (session.Subscription!.CollectionMethod, session.Subscription.DaysUntilDue).ShouldBe((CollectionMethod.SendInvoice, 0));
        page.QuerySelector("#invoiced")!.TextContent.ShouldBe("Renewal invoices are emailed to you and paid on their page.");
        page.QuerySelector("#card-storage").ShouldBeNull();
        (cardTrial.StatusCode, await cardTrial.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "checkout.not_supported_by_connection"));
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var subscription = await GetSubscriptionAsync(api, stored.SubscriptionId!.Value);
        (subscription.Status, subscription.CollectionMethod, subscription.DaysUntilDue).ShouldBe((Contracts.SubscriptionStatus.Active, CollectionMethod.SendInvoice, 0));
        (await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{stored.InvoiceId}")).Status.ShouldBe(Contracts.InvoiceStatus.Paid);
    }

    [Theory]
    [Trait("Requirement", "CHK-1")]
    [InlineData("setup-fee-try,team-monthly-try,team-yearly-try", "items[2]")]
    [InlineData("pro-monthly-try,setup-fee-try", "items[1].price")]
    [InlineData("setup-fee-try", "items")]
    public async Task ASubscriptionSession_SellsRecurringPricesOfOneInterval_AndOneTimePricesOnlyWithoutATrial(string prices, string field)
    {
        await using var host = Host();
        using var api = await ClientAsync(host);

        using var response = await api.PostJsonAsync("/api/v1/checkout-sessions", Request(prices.Split(',')));
        var problem = await response.ReadAsync<JsonElement>();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (problem.GetProperty("code").GetString(), problem.GetProperty("errors").EnumerateObject().Single().Name).ShouldBe(("checkout.invalid_items", field));
    }

    private static Dictionary<string, string> WithCardConsent()
    {
        var form = Form("ada@example.com", "Ada Lovelace", Documents);
        form["Form.CardStorage"] = "true";
        return form;
    }

    private static async Task<CheckoutSessionResponse> SessionAsync(HttpClient api, IReadOnlyList<string> prices, CheckoutSubscriptionData? subscription = null)
    {
        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", Request(prices, subscription));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return await created.ReadAsync<CheckoutSessionResponse>();
    }

    private static CreateCheckoutSessionRequest Request(IReadOnlyList<string> prices, CheckoutSubscriptionData? subscription = null) => new()
    {
        Mode = CheckoutMode.Subscription,
        Items = [.. prices.Select(price => new CheckoutItemRequest { Price = price })],
        Subscription = subscription,
        CustomerDetails = new CheckoutCustomerDetails { Locale = "en" },
        SuccessUrl = "https://shop.example.com/done",
    };

    private static async Task SimulatorSavesCardsAsync(HttpClient api, string savesCards)
    {
        using var updated = await api.PatchJsonAsync("/api/v1/provider-connections/simulator", new { settings = new Dictionary<string, string> { ["savesCards"] = savesCards } });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host() =>
        IsolatedHosts.Create(server, server.NewDatabase("subscription_checkout"), _time, services => services.AddSingleton<IEmailSender>(_email));
}
