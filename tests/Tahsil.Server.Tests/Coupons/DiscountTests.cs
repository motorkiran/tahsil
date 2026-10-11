using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Domain.Money;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Coupons;

/// <summary>
/// Discounts on subscriptions and manual invoices (CPN §4, §5) on a fake clock: monthly subscriptions start on April 1
/// and renew on the first of each month at midnight, finalized an hour later after the draft period.
/// </summary>
public sealed class DiscountTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CPN-11")]
    [Trait("Requirement", "CPN-12")]
    [Trait("Requirement", "MNY-18")]
    public async Task ARepeatingCode_DiscountsExactlyItsCycleInvoices_AndTheProrationsBetween()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CouponAsync(client, new CreateCouponRequest { Key = "launch", Name = "Launch discount", PercentOff = 50, Duration = CouponDuration.Repeating, DurationInCycles = 3 });
        await CodeAsync(client, "launch", "LAUNCH50");
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }], PromotionCode = "launch50" });
        await RenewAsync(host, April.AddMonths(1));
        await AdvanceToAsync(host, April.AddMonths(1).AddDays(14));
        using (var upgraded = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { items = new[] { new { price = "team-plus-monthly-try" } } }))
        {
            upgraded.StatusCode.ShouldBe(HttpStatusCode.OK, await upgraded.Content.ReadAsStringAsync(CancellationToken));
        }

        var beforeLastCycle = await GetSubscriptionAsync(client, subscription.Id);
        await RenewAsync(host, April.AddMonths(2));
        await RenewAsync(host, April.AddMonths(3));

        subscription.Discount!.CyclesRemaining.ShouldBe(2);
        beforeLastCycle.Discount!.CyclesRemaining.ShouldBe(1, "a proration invoice does not count a cycle");
        (await GetSubscriptionAsync(client, subscription.Id)).Discount.ShouldBeNull();
        var invoices = await InvoicesAsync(client, subscription.Id);
        invoices.Select(invoice => (invoice.BillingReason, invoice.Discounts.Count)).ShouldBe(
        [
            (BillingReason.SubscriptionCreate, 1), (BillingReason.SubscriptionCycle, 1), (BillingReason.SubscriptionUpdate, 1),
            (BillingReason.SubscriptionCycle, 1), (BillingReason.SubscriptionCycle, 0),
        ]);
        foreach (var invoice in invoices)
        {
            invoice.Lines.ShouldAllBe(line => line.DiscountAmount == (invoice.Discounts.Count == 0 ? 0 : LineArithmetic.PercentageDiscount(line.Amount, 50)));
        }

        invoices[0].Discounts.ShouldHaveSingleItem().ShouldBe(new InvoiceDiscountResponse(subscription.Discount.Coupon.Id, subscription.Discount.PromotionCodeId, "Launch discount", 15_000));
        (invoices[0].Total, invoices[^1].Total).ShouldBe((15_000L, 60_000L));
        (await GetAsync<CouponResponse>(client, "/api/v1/coupons/launch")).TimesRedeemed.ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "CPN-9")]
    public async Task ConcurrentRedemptions_AtTheLimit_NeverExceedIt()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CouponAsync(client, new CreateCouponRequest { Key = "first-three", Name = "First three", PercentOff = 10, Duration = CouponDuration.Forever, MaxRedemptions = 3 });
        await CouponAsync(client, new CreateCouponRequest { Key = "open", Name = "Open", PercentOff = 10, Duration = CouponDuration.Forever });
        await CodeAsync(client, "open", "FIRST-TWO", maxRedemptions: 2);
        var customers = new List<Guid>();
        for (var index = 0; index < 12; index++)
        {
            customers.Add(await CustomerAsync(client));
        }

        var byCoupon = await Task.WhenAll(customers.Take(6).Select(customer => SubscribeAsync(client, customer, coupon: "first-three")));
        var byCode = await Task.WhenAll(customers.Skip(6).Select(customer => SubscribeAsync(client, customer, promotionCode: "first-two")));

        byCoupon.Count(code => code is null).ShouldBe(3);
        byCoupon.Where(code => code is not null).ShouldAllBe(code => code == "coupon.max_redemptions_reached");
        byCode.Count(code => code is null).ShouldBe(2);
        byCode.Where(code => code is not null).ShouldAllBe(code => code == "promotion_code.max_redemptions_reached");
        (await GetAsync<CouponResponse>(client, "/api/v1/coupons/first-three")).TimesRedeemed.ShouldBe(3);
        (await GetAsync<Page<PromotionCodeResponse>>(client, "/api/v1/promotion-codes?code=first-two")).Items.Single().TimesRedeemed.ShouldBe(2);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.discounts")).ShouldBe([5]);
    }

    [Fact]
    [Trait("Requirement", "CPN-10")]
    [Trait("Requirement", "CPN-14")]
    public async Task ADiscount_IsReplaced_ThenRemoved_AndAnOpenDraftDropsIt()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CouponAsync(client, new CreateCouponRequest { Key = "loyal", Name = "Loyal", PercentOff = 10, Duration = CouponDuration.Forever });
        await CouponAsync(client, new CreateCouponRequest { Key = "spring", Name = "Spring", PercentOff = 20, Duration = CouponDuration.Forever });
        await CodeAsync(client, "spring", "SPRING20");
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client));
        using (var discounted = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { coupon = "loyal" }))
        {
            (await discounted.ReadAsync<SubscriptionResponse>()).Discount!.Coupon.Key.ShouldBe("loyal");
        }

        using var replaced = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { promotionCode = "spring20" });
        await AdvanceToAsync(host, April.AddMonths(1));
        var draft = (await InvoicesAsync(client, subscription.Id))[^1];
        using var removed = await client.DeleteAsync(new Uri($"/api/v1/subscriptions/{subscription.Id}/discount", UriKind.Relative), CancellationToken);
        await AdvanceToAsync(host, April.AddMonths(1).AddHours(1));

        (await replaced.ReadAsync<SubscriptionResponse>()).Discount!.Coupon.Key.ShouldBe("spring");
        (draft.Status, draft.DiscountTotal).ShouldBe((InvoiceStatus.Draft, 6_000L));
        (await removed.ReadAsync<SubscriptionResponse>()).Discount.ShouldBeNull();
        var finalized = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{draft.Id}");
        (finalized.Status, finalized.DiscountTotal, finalized.Discounts.Count).ShouldBe((InvoiceStatus.Open, 0L, 0));
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE action LIKE 'discount.%'"))
            .ShouldBe(["discount.applied", "discount.removed", "discount.applied", "discount.removed"], ignoreOrder: true);
    }

    [Fact]
    [Trait("Requirement", "CPN-8")]
    public async Task ACouponForSomeProducts_DiscountsOnlyTheirItems_AndIsRefusedWithoutThem()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CouponAsync(client, new CreateCouponRequest { Key = "growth-100", Name = "Growth 100", AmountOff = 10_000, Currency = "TRY", Duration = CouponDuration.Once, Products = ["growth"] });
        await CouponAsync(client, new CreateCouponRequest { Key = "pro-only", Name = "Pro only", PercentOff = 50, Duration = CouponDuration.Once, Products = ["pro"] });
        await CouponAsync(client, new CreateCouponRequest { Key = "dollars", Name = "Dollars", AmountOff = 1_000, Currency = "USD", Duration = CouponDuration.Once });
        var customer = await CustomerAsync(client);
        var items = new List<SubscriptionItemRequest> { new() { Price = "team-monthly-try" }, new() { Price = "starter-monthly-try" } };

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = items,
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 30,
            Coupon = "growth-100",
        });
        using var otherProducts = await client.PostJsonAsync("/api/v1/subscriptions", new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = items,
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 30,
            Coupon = "pro-only",
        });
        using var otherCurrency = await client.PostJsonAsync("/api/v1/subscriptions", new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = items,
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 30,
            Coupon = "dollars",
        });

        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{subscription.LatestInvoice!.Id}");
        invoice.Lines.Select(line => (line.Amount, line.DiscountAmount)).ShouldBe([(30_000L, 0L), (29_900L, 10_000L)]);
        (otherProducts.StatusCode, await otherProducts.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "coupon.not_applicable"));
        (otherCurrency.StatusCode, await otherCurrency.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "coupon.currency_mismatch"));
        (await GetAsync<Page<SubscriptionResponse>>(client, $"/api/v1/subscriptions?customer={customer}")).Items.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Requirement", "CPN-5")]
    public async Task AFirstTimeCode_IsRefusedToACustomerWhoHasPaid()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CouponAsync(client, new CreateCouponRequest { Key = "welcome", Name = "Welcome", PercentOff = 30, Duration = CouponDuration.Once });
        await CodeAsync(client, "welcome", "WELCOME", firstTimeOnly: true);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });

        using var refused = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { promotionCode = "welcome" });
        using var unknown = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { promotionCode = "nothing" });

        (refused.StatusCode, await refused.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "promotion_code.not_eligible"));
        (unknown.StatusCode, await unknown.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "promotion_code.not_found"));
        (await GetAsync<Page<PromotionCodeResponse>>(client, "/api/v1/promotion-codes?code=welcome")).Items.Single().TimesRedeemed.ShouldBe(0);
    }

    [Fact]
    [Trait("Requirement", "CPN-9")]
    [Trait("Requirement", "CPN-13")]
    public async Task AManualInvoicesCoupon_IsCountedWhenTheInvoiceIsFinalized()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CouponAsync(client, new CreateCouponRequest { Key = "fifty", Name = "Fifty off", AmountOff = 5_000, Currency = "TRY", Duration = CouponDuration.Once });
        await CouponAsync(client, new CreateCouponRequest { Key = "dollars", Name = "Dollars", AmountOff = 1_000, Currency = "USD", Duration = CouponDuration.Once });
        var customer = await CustomerAsync(client);

        using var otherCurrency = await client.PostJsonAsync("/api/v1/invoices", Manual(customer, "dollars"));
        using var created = await client.PostJsonAsync("/api/v1/invoices", Manual(customer, "fifty"));
        var draft = await created.ReadAsync<InvoiceResponse>();
        var redeemedBefore = (await GetAsync<CouponResponse>(client, "/api/v1/coupons/fifty")).TimesRedeemed;
        using var finalized = await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize");

        (otherCurrency.StatusCode, await otherCurrency.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "coupon.currency_mismatch"));
        (draft.DiscountTotal, draft.Total).ShouldBe((5_000L, 7_500L));
        redeemedBefore.ShouldBe(0);
        var invoice = await finalized.ReadAsync<InvoiceResponse>();
        (invoice.Status, invoice.Total, invoice.Discounts.Single().Name).ShouldBe((InvoiceStatus.Open, 7_500L, "Fifty off"));
        (await GetAsync<CouponResponse>(client, "/api/v1/coupons/fifty")).TimesRedeemed.ShouldBe(1);
        (await host.QueryAsync<Guid>($"SELECT invoice_id AS \"Value\" FROM tahsil.discounts WHERE ended_at IS NOT NULL")).ShouldBe([draft.Id]);
    }

    private static CreateInvoiceRequest Manual(Guid customer, string coupon) => new()
    {
        Customer = customer,
        Currency = "TRY",
        Coupon = coupon,
        Lines = [new InvoiceLineRequest { Description = "Consulting", UnitAmount = 12_500 }],
    };

    /// <summary>The problem code of a send_invoice subscription's creation with a discount, or <see langword="null"/> when it was created.</summary>
    private static async Task<string?> SubscribeAsync(HttpClient client, Guid customer, string? coupon = null, string? promotionCode = null)
    {
        using var response = await client.PostJsonAsync("/api/v1/subscriptions", new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new() { Price = "team-monthly-try" }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 30,
            Coupon = coupon,
            PromotionCode = promotionCode,
        });
        return response.StatusCode == HttpStatusCode.Created ? null : await response.ProblemCodeAsync();
    }

    private static async Task<List<InvoiceResponse>> InvoicesAsync(HttpClient client, Guid subscription) =>
        [.. (await GetAsync<Page<InvoiceResponse>>(client, $"/api/v1/invoices?subscription={subscription}")).Items.OrderBy(invoice => invoice.CreatedAt).ThenBy(invoice => invoice.Id)];

    private static async Task CouponAsync(HttpClient client, CreateCouponRequest request)
    {
        using var response = await client.PostJsonAsync("/api/v1/coupons", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task CodeAsync(HttpClient client, string coupon, string code, int? maxRedemptions = null, bool firstTimeOnly = false)
    {
        using var response = await client.PostJsonAsync("/api/v1/promotion-codes", new CreatePromotionCodeRequest
        {
            Coupon = coupon,
            Code = code,
            MaxRedemptions = maxRedemptions,
            FirstTimeOnly = firstTimeOnly,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>Moves to a renewal, then past its draft period, so the cycle invoice is finalized and collected.</summary>
    private async Task RenewAsync(WebApplicationFactory<Program> host, DateTimeOffset renewal)
    {
        await AdvanceToAsync(host, renewal);
        await AdvanceToAsync(host, renewal.AddHours(1));
    }

    private async Task AdvanceToAsync(WebApplicationFactory<Program> host, DateTimeOffset instant)
    {
        _time.Advance(instant - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("discounts"), _time);
}
