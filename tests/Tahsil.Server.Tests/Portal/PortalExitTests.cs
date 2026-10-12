using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Server.Tests.Notifications;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Portal;

/// <summary>
/// The M6 exit scenario, with email sent over SMTP to Mailpit: a customer subscribes to monthly Team (₺300.00) on
/// April 1 with a code for half off three months, upgrades to Team Plus (₺600.00) on April 16, halfway through the
/// period, gives a new card that pays the May renewal, and cancels on May 10; every step's invoice, refund and email is
/// checked, and the withdrawal that the customer's consent to start at once ended is never offered.
/// </summary>
[Trait("Suite", "Exit")]
public sealed class PortalExitTests(TahsilServerFixture server, Mailpit mailpit) : IClassFixture<Mailpit>
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Documents = ["pre-information", "distance-sales"];

    private readonly FakeTimeProvider _time = new(April);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CHK-9")]
    [Trait("Requirement", "CPN-11")]
    [Trait("Requirement", "PRT-6")]
    [Trait("Requirement", "PRT-8")]
    [Trait("Requirement", "PRT-10")]
    [Trait("Requirement", "PRT-11")]
    public async Task ACustomer_RedeemsACode_ChangesPlan_UpdatesTheCard_AndCancels_InThePortal()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var email = $"customer-{Guid.CreateVersion7():N}@example.com";
        using (var coupon = await api.PostJsonAsync("/api/v1/coupons", new CreateCouponRequest
        {
            Key = "launch",
            Name = "Launch",
            PercentOff = 50,
            Duration = CouponDuration.Repeating,
            DurationInCycles = 3,
        }))
        {
            coupon.StatusCode.ShouldBe(HttpStatusCode.Created, await coupon.Content.ReadAsStringAsync(CancellationToken));
        }

        using (var code = await api.PostJsonAsync("/api/v1/promotion-codes", new CreatePromotionCodeRequest { Coupon = "launch", Code = "LAUNCH50" }))
        {
            code.StatusCode.ShouldBe(HttpStatusCode.Created, await code.Content.ReadAsStringAsync(CancellationToken));
        }

        // April 1: the code at checkout, with the consent to start at once.
        var checkout = await CheckoutAsync(api);
        using var browser = Open(host);
        using (var applied = await PostHandlerAsync(browser, checkout.Url!, "Code", ("PromotionCode", "launch50")))
        {
            applied.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        var form = Form(email, "Deniz Kaya", Documents, immediateStart: true);
        form["Form.CardStorage"] = "true";
        using (var paid = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, checkout.Url!, form), "4242424242424242"))
        {
            paid.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await host.RunWorkersUntilIdleAsync();
        var completed = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(session => session.Id == checkout.Id, CancellationToken));
        var customer = completed.CustomerId!.Value;
        var subscription = await GetSubscriptionAsync(api, completed.SubscriptionId!.Value);
        var first = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{completed.InvoiceId}");

        // April 9: no withdrawal, as the service started at once with the customer's consent.
        await AdvanceToAsync(host, April.AddDays(8));
        var early = await ParseAsync(await browser.GetAsync(await PortalUrlAsync(api, customer, $"subscriptions/{subscription.Id}/cancel"), CancellationToken));

        // April 16: the upgrade, discounted like the subscription.
        await AdvanceToAsync(host, April.AddDays(15));
        var plan = await PortalUrlAsync(api, customer, $"subscriptions/{subscription.Id}/change");
        var plus = (await GetAsync<PriceResponse>(api, "/api/v1/prices/team-plus-monthly-try")).Id;
        var preview = await ParseAsync(await PostHandlerAsync(browser, plan, "Preview", ("Price", plus.ToString()), ("Quantity", "1")));
        using (var upgraded = await PostHandlerAsync(browser, plan, "Confirm", ("Price", plus.ToString()), ("Quantity", "1")))
        {
            upgraded.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        await host.RunWorkersUntilIdleAsync();
        var upgrade = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{(await GetSubscriptionAsync(api, subscription.Id)).LatestInvoice!.Id}");

        // April 20: a new card, which pays the renewal on May 1.
        await AdvanceToAsync(host, April.AddDays(19));
        var cards = await PortalUrlAsync(api, customer, "payment-method");
        using (var added = await PayOnTheEmbedAsync(host, browser, await PostHandlerAsync(browser, cards, "Add", ("CardStorage", "true")), "4000000000007726"))
        {
            added.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var card = (await GetAsync<Page<PaymentMethodResponse>>(api, $"/api/v1/customers/{customer}/payment-methods")).Items.Single(method => method.Last4 == "7726");
        await AdvanceToAsync(host, April.AddMonths(1));
        await AdvanceToAsync(host, April.AddMonths(1).AddHours(1));
        var renewed = await GetSubscriptionAsync(api, subscription.Id);
        var renewal = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{renewed.LatestInvoice!.Id}");
        var renewalPayment = (await GetAsync<Page<PaymentResponse>>(api, $"/api/v1/payments?invoice={renewal.Id}")).Items.ShouldHaveSingleItem();

        // May 10: cancelled at once, with the unused 22 of May's 31 days refunded.
        await AdvanceToAsync(host, April.AddMonths(1).AddDays(9));
        var cancel = await PortalUrlAsync(api, customer, $"subscriptions/{subscription.Id}/cancel");
        var choices = await ParseAsync(await browser.GetAsync(cancel, CancellationToken));
        using (var canceled = await PostHandlerAsync(browser, cancel, string.Empty, ("Choice", "Now"), ("Feedback", "TooExpensive")))
        {
            canceled.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        var refundedAt = _time.GetUtcNow();

        // The new card's bank takes an hour to confirm the refund, which reconciliation then learns.
        await AdvanceToAsync(host, refundedAt.AddHours(2));

        (first.Status, first.Subtotal, first.Total, first.Discounts.Single().Name).ShouldBe((InvoiceStatus.Paid, 30_000L, 15_000L, "Launch"));
        early.QuerySelectorAll("#choices .choice").Select(choice => choice.TextContent.Trim()).ShouldBe(
            ["At the end of the period (May 1, 2026)", "Now, with a refund of ₺110.00 for the unused time"], "the withdrawal ended when the service started at once");
        preview.QuerySelector("#confirm")!.TextContent.ShouldBe("Pay ₺75.00 and upgrade");
        (upgrade.Status, upgrade.BillingReason, upgrade.Total).ShouldBe((InvoiceStatus.Paid, BillingReason.SubscriptionUpdate, 7_500L));
        renewed.DefaultPaymentMethodId.ShouldBe(card.Id);
        (renewal.Status, renewal.BillingReason, renewal.Subtotal, renewal.Total).ShouldBe((InvoiceStatus.Paid, BillingReason.SubscriptionCycle, 60_000L, 30_000L));
        renewalPayment.PaymentMethodId.ShouldBe(card.Id);
        choices.QuerySelectorAll("#choices .choice").Select(choice => choice.TextContent.Trim()).ShouldBe(
            ["At the end of the period (June 1, 2026)", "Now, with a refund of ₺212.90 for the unused time"]);
        (await GetSubscriptionAsync(api, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Canceled);
        var refund = (await GetAsync<Page<RefundResponse>>(api, $"/api/v1/refunds?payment={renewalPayment.Id}")).Items.ShouldHaveSingleItem();
        (refund.Amount, refund.Status).ShouldBe((21_290L, RefundStatus.Succeeded));
        var received = await mailpit.ReceivedByAsync(email);
        received.Select(message => message.Subject).ShouldBe(
            [
                $"Your subscription is confirmed ({first.Number})",
                "Your plan changed",
                $"Payment received: {upgrade.Number}",
                $"Payment received: {renewal.Number}",
                "Your subscription ended",
                "Your refund was issued: ₺212.90",
            ],
            ignoreOrder: true,
            "each step's emails once, and no receipt beside the order confirmation");
        received.Single(message => message.Subject == "Your plan changed").Text.ShouldContain("₺75.00 was charged");
        received.Single(message => message.Subject == "Your subscription ended").Text.ShouldContain("₺212.90 is being refunded to you.");
    }

    private static async Task<CheckoutSessionResponse> CheckoutAsync(HttpClient api)
    {
        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Subscription,
            Items = [new CheckoutItemRequest { Price = "team-monthly-try" }],
            AllowPromotionCodes = true,
            CustomerDetails = new CheckoutCustomerDetails { Locale = "en" },
            SuccessUrl = "https://shop.example.com/done",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return await created.ReadAsync<CheckoutSessionResponse>();
    }

    /// <summary>A page of a new portal session, as the application would open it for the customer.</summary>
    private static async Task<Uri> PortalUrlAsync(HttpClient api, Guid customer, string path)
    {
        using var response = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "en" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return new Uri($"{(await response.ReadAsync<PortalSessionResponse>()).Url}/{path}");
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private async Task AdvanceToAsync(WebApplicationFactory<Program> host, DateTimeOffset instant)
    {
        _time.Advance(instant - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
    }

    private WebApplicationFactory<Program> Host()
    {
        var settings = mailpit.Settings;
        settings["Tahsil:Portal:Products:0:Product"] = "team";
        settings["Tahsil:Portal:Features:withdrawal"] = "true";
        return IsolatedHosts.Create(server, server.NewDatabase("portal_exit"), _time, settings: settings);
    }
}
