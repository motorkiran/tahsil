using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using CheckoutStatus = Tahsil.Domain.Checkout.CheckoutStatus;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>Coupons and promotion codes on checkout sessions (CHK-7, CHK-9, CPN §3, §7), with the simulator.</summary>
public sealed class CheckoutDiscountTests(TahsilServerFixture server)
{
    private static readonly string[] Documents = ["pre-information", "distance-sales"];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CHK-7")]
    [Trait("Requirement", "CPN-9")]
    [Trait("Requirement", "CPN-13")]
    public async Task ACouponFromTheApi_DiscountsAPurchase_AndIsCountedWhenItCompletes()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await CouponAsync(api, new CreateCouponRequest { Key = "welcome", Name = "Welcome", PercentOff = 25, Duration = CouponDuration.Once });
        var session = await SessionAsync(api, CheckoutMode.Payment, "setup-fee-try", coupon: "welcome");
        using var browser = Open(host);

        var page = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        var redeemedBefore = (await GetAsync<CouponResponse>(api, "/api/v1/coupons/welcome")).TimesRedeemed;
        using var returned = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, Form("ada@example.com", "Ada Lovelace", Documents)), "4242424242424242");

        (session.Quote.Subtotal, session.Quote.DiscountTotal, session.Quote.AmountDue, session.PromotionCodeId).ShouldBe((10_000L, 2_500L, 7_500L, null));
        page.QuerySelector("#discount")!.TextContent.ShouldBe("Welcome (−25%)−₺25.00");
        page.QuerySelector("#pay")!.TextContent.Trim().ShouldBe("Pay ₺75.00");
        page.QuerySelector("#promotion-code").ShouldBeNull("the session does not let customers enter codes");
        redeemedBefore.ShouldBe(0);
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var invoice = await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{stored.InvoiceId}");
        (stored.Status, invoice.Status, invoice.Total, invoice.AmountPaid).ShouldBe((CheckoutStatus.Complete, InvoiceStatus.Paid, 7_500L, 7_500L));
        invoice.Discounts.ShouldHaveSingleItem().ShouldBe(new InvoiceDiscountResponse(session.CouponId!.Value, null, "Welcome", 2_500));
        (await GetAsync<CouponResponse>(api, "/api/v1/coupons/welcome")).TimesRedeemed.ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "CHK-9")]
    [Trait("Requirement", "CPN-11")]
    public async Task ACodeTheCustomerEnters_DiscountsTheSubscription_AndCanBeTakenOff()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await CouponAsync(api, new CreateCouponRequest { Key = "launch", Name = "Launch discount", PercentOff = 50, Duration = CouponDuration.Repeating, DurationInCycles = 3 });
        await CodeAsync(api, "launch", "LAUNCH50");
        var session = await SessionAsync(api, CheckoutMode.Subscription, "team-monthly-try", allowCodes: true);
        using var browser = Open(host);

        using var applied = await PostHandlerAsync(browser, session.Url!, "Code", ("PromotionCode", " launch50 "));
        var discounted = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        using var removed = await PostHandlerAsync(browser, session.Url!, "RemoveCode");
        var plain = await ParseAsync(await browser.GetAsync(session.Url, CancellationToken));
        using var again = await PostHandlerAsync(browser, session.Url!, "Code", ("PromotionCode", "LAUNCH50"));
        var form = Form("ada@example.com", "Ada Lovelace", Documents);
        form["Form.CardStorage"] = "true";
        using var returned = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, form), "4242424242424242");

        (applied.StatusCode, removed.StatusCode, again.StatusCode).ShouldBe((HttpStatusCode.Redirect, HttpStatusCode.Redirect, HttpStatusCode.Redirect));
        discounted.QuerySelector("#discount")!.TextContent.ShouldBe("Launch discount (−50%, 3 months)−₺150.00");
        discounted.QuerySelector("#pay")!.TextContent.Trim().ShouldBe("Pay ₺150.00 and subscribe");
        discounted.QuerySelector("#renewal")!.TextContent.ShouldStartWith("Then ₺300.00 monthly", Case.Sensitive, "a discount that ends is not what every renewal bills");
        plain.QuerySelector("#discount").ShouldBeNull();
        plain.QuerySelector("#pay")!.TextContent.Trim().ShouldBe("Pay ₺300.00 and subscribe");
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var subscription = await GetSubscriptionAsync(api, stored.SubscriptionId!.Value);
        var code = (await GetAsync<Page<PromotionCodeResponse>>(api, "/api/v1/promotion-codes?code=launch50")).Items.Single();
        (subscription.Discount!.Coupon.Key, subscription.Discount.PromotionCodeId, subscription.Discount.CyclesRemaining).ShouldBe(("launch", (Guid?)code.Id, 2));
        (await GetAsync<InvoiceResponse>(api, $"/api/v1/invoices/{stored.InvoiceId}")).Total.ShouldBe(15_000);
        (code.TimesRedeemed, stored.PromotionCodeAttempts).ShouldBe((1, 2));
    }

    [Fact]
    [Trait("Requirement", "CHK-9")]
    public async Task CodesThatCannotBeUsed_AreRefusedAlike_AndGuessingIsLimited()
    {
        await using var host = Host(IPAddress.Parse("203.0.113.7"));
        using var api = await ClientAsync(host);
        await CouponAsync(api, new CreateCouponRequest { Key = "spring", Name = "Spring", PercentOff = 10, Duration = CouponDuration.Once });
        await CouponAsync(api, new CreateCouponRequest { Key = "all", Name = "Everything", PercentOff = 100, Duration = CouponDuration.Once });
        var inactive = await CodeAsync(api, "spring", "SPRING10");
        await CodeAsync(api, "all", "ALLFREE");
        using (var deactivated = await api.PatchJsonAsync($"/api/v1/promotion-codes/{inactive}", new { isActive = false }))
        {
            deactivated.EnsureSuccessStatusCode();
        }

        var session = await SessionAsync(api, CheckoutMode.Payment, "setup-fee-try", allowCodes: true);
        var withoutField = await SessionAsync(api, CheckoutMode.Payment, "setup-fee-try");
        using var browser = Open(host);

        var messages = new List<(HttpStatusCode, string?)>();
        foreach (var code in new[] { "NOSUCHCODE", "SPRING10", "ALLFREE", "" })
        {
            using var refused = await PostHandlerAsync(browser, session.Url!, "Code", ("PromotionCode", code));
            messages.Add((refused.StatusCode, (await ParseAsync(refused)).QuerySelector("#checkout-error")?.TextContent));
        }

        for (var attempt = 5; attempt <= 10; attempt++)
        {
            (await PostHandlerAsync(browser, session.Url!, "Code", ("PromotionCode", $"GUESS{attempt}"))).Dispose();
        }

        using var eleventh = await PostHandlerAsync(browser, session.Url!, "Code", ("PromotionCode", "GUESS11"));
        using var notOffered = await PostHandlerAsync(browser, withoutField.Url!, "Code", ("PromotionCode", "GUESS"));

        messages.ShouldBe(Enumerable.Repeat<(HttpStatusCode, string?)>((HttpStatusCode.BadRequest, "This code cannot be used."), 4));
        (eleventh.StatusCode, (await ParseAsync(eleventh)).QuerySelector("#checkout-error")!.TextContent).ShouldBe((HttpStatusCode.TooManyRequests, "Too many codes were tried. Please try again later."));
        notOffered.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await QueryAsync(host, context => context.CheckoutSessions.Where(candidate => candidate.Id == session.Id).Select(candidate => candidate.CouponId).SingleAsync(CancellationToken))).ShouldBeNull();

        // Two more sessions use up the network's 30 attempts of the hour; the next session's first is refused.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var other = await SessionAsync(api, CheckoutMode.Payment, "setup-fee-try", allowCodes: true);
            foreach (var attempt in Enumerable.Range(0, 10))
            {
                (await PostHandlerAsync(browser, other.Url!, "Code", ("PromotionCode", $"GUESS{attempt}"))).Dispose();
            }
        }

        var fresh = await SessionAsync(api, CheckoutMode.Payment, "setup-fee-try", allowCodes: true);
        using var limited = await PostHandlerAsync(browser, fresh.Url!, "Code", ("PromotionCode", "GUESS"));
        _time.Advance(TimeSpan.FromMinutes(61));
        using var later = await PostHandlerAsync(browser, fresh.Url!, "Code", ("PromotionCode", "GUESS"));
        (limited.StatusCode, later.StatusCode).ShouldBe((HttpStatusCode.TooManyRequests, HttpStatusCode.BadRequest));
    }

    [Fact]
    [Trait("Requirement", "CPN-5")]
    [Trait("Requirement", "CPN-8")]
    public async Task ADiscountThatDoesNotFit_IsRefusedAtCreation_OrTakenOffOnceTheCustomerIsKnown()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await CouponAsync(api, new CreateCouponRequest { Key = "growth-only", Name = "Growth only", PercentOff = 10, Duration = CouponDuration.Once, Products = ["growth"] });
        await CouponAsync(api, new CreateCouponRequest { Key = "first", Name = "First purchase", PercentOff = 10, Duration = CouponDuration.Once });
        await CodeAsync(api, "first", "FIRST10", firstTimeOnly: true);
        Guid customer;
        using (var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { ExternalId = "returning", Email = "ayse@example.com", Name = "Ayşe Yılmaz" }))
        {
            customer = (await created.ReadAsync<CustomerResponse>()).Id;
        }

        await SaveCardAsync(api, customer, "4242424242424242");
        await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });

        using var otherProducts = await api.PostJsonAsync("/api/v1/checkout-sessions", Request(CheckoutMode.Payment, "setup-fee-try", coupon: "growth-only"));
        using var both = await api.PostJsonAsync("/api/v1/checkout-sessions", Request(CheckoutMode.Payment, "setup-fee-try", coupon: "first") with { PromotionCode = "FIRST10" });
        using var setup = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest { Mode = CheckoutMode.Setup, Currency = "TRY", Coupon = "first", SuccessUrl = "https://shop.example.com/done" });
        using var known = await api.PostJsonAsync("/api/v1/checkout-sessions", Request(CheckoutMode.Payment, "setup-fee-try") with { Customer = customer, CustomerDetails = null, PromotionCode = "first10" });

        // The session does not know its customer yet; the returning customer's details find them when the form is submitted.
        var session = await SessionAsync(api, CheckoutMode.Payment, "setup-fee-try", promotionCode: "FIRST10", externalId: "returning");
        using var browser = Open(host);
        using var submitted = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe Yılmaz", Documents));
        var page = await ParseAsync(submitted);

        (otherProducts.StatusCode, await otherProducts.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "coupon.not_applicable"));
        (both.StatusCode, await both.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "coupon.invalid"));
        (setup.StatusCode, await setup.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "checkout.invalid"));
        (known.StatusCode, await known.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "promotion_code.not_eligible"));
        session.Quote.AmountDue.ShouldBe(9_000);
        submitted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        page.QuerySelector("#checkout-error")!.TextContent.ShouldBe("The discount does not apply to you and was removed. Check the total and continue.");
        page.QuerySelector("#total-due")!.TextContent.ShouldBe("₺100.00");
        (await QueryAsync(host, context => context.Payments.CountAsync(payment => payment.CheckoutSessionId == session.Id, CancellationToken))).ShouldBe(0);
    }

    private static async Task<CheckoutSessionResponse> SessionAsync(
        HttpClient api, CheckoutMode mode, string price, string? coupon = null, string? promotionCode = null, bool allowCodes = false, string? externalId = null)
    {
        using var created = await api.PostJsonAsync(
            "/api/v1/checkout-sessions",
            Request(mode, price, coupon) with { PromotionCode = promotionCode, AllowPromotionCodes = allowCodes, CustomerDetails = new CheckoutCustomerDetails { Locale = "en", ExternalId = externalId } });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return await created.ReadAsync<CheckoutSessionResponse>();
    }

    private static CreateCheckoutSessionRequest Request(CheckoutMode mode, string price, string? coupon = null) => new()
    {
        Mode = mode,
        Items = [new CheckoutItemRequest { Price = price }],
        Coupon = coupon,
        CustomerDetails = new CheckoutCustomerDetails { Locale = "en" },
        SuccessUrl = "https://shop.example.com/done",
    };

    private static async Task CouponAsync(HttpClient api, CreateCouponRequest request)
    {
        using var response = await api.PostJsonAsync("/api/v1/coupons", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<Guid> CodeAsync(HttpClient api, string coupon, string code, bool firstTimeOnly = false)
    {
        using var response = await api.PostJsonAsync("/api/v1/promotion-codes", new CreatePromotionCodeRequest { Coupon = coupon, Code = code, FirstTimeOnly = firstTimeOnly });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.ReadAsync<PromotionCodeResponse>()).Id;
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host(IPAddress? clientAddress = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("checkout_discounts"), _time, services =>
        {
            if (clientAddress is not null)
            {
                services.AddSingleton<IStartupFilter>(new ClientAddress(clientAddress));
            }
        });
}
