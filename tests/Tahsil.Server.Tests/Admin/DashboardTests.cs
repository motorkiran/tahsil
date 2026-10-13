using System.Net;
using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Admin.Components.Pages;
using Tahsil.Admin.Security;
using Tahsil.Application.Dashboard;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Admin;

/// <summary>The dashboard's figures (ADM §3, ADM-5, ADM-6), computed from what the API created.</summary>
public sealed class DashboardTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero));

    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "ADM-5")]
    [Trait("Requirement", "ADM-6")]
    public async Task TheFigures_CountEachCurrencyByItself_AndAreKeptForFiveMinutes()
    {
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("dashboard"), _time);
        using var api = await ClientAsync(host);
        await ProvisionAsync(api);

        // Two seats a month, a yearly plan, and a seat at half price: prices include 20% KDV, which MRR leaves out.
        await SubscribeAsync(api, "team-monthly-try", quantity: 2);
        await SubscribeAsync(api, "team-yearly-try");
        await SubscribeAsync(api, "team-monthly-try", coupon: "half");

        // A trial, a subscription in another currency, one that ended today, and one whose first payment failed.
        await SubscribeAsync(api, "pro-monthly-try");
        var abroad = await CompanyAsync(api);
        await CreateAsync(api, new CreateSubscriptionRequest
        {
            Customer = abroad,
            Items = [new SubscriptionItemRequest { Price = "team-monthly-usd", Quantity = 3 }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 30,
        });
        var left = await SubscribeAsync(api, "team-monthly-try");
        using (var canceled = await api.PostJsonAsync($"/api/v1/subscriptions/{left.Id}/cancel", new CancelSubscriptionRequest { At = "now" }))
        {
            canceled.StatusCode.ShouldBe(HttpStatusCode.OK, await canceled.Content.ReadAsStringAsync(CancellationToken));
        }

        var declined = await CompanyAsync(api);
        await SaveCardAsync(api, declined, "4242424242424242", behavior: "card_declined");
        (await CreateAsync(api, new CreateSubscriptionRequest { Customer = declined, Items = [new SubscriptionItemRequest { Price = "team-monthly-try" }] })).Status.ShouldBe(SubscriptionStatus.Incomplete);

        var figures = await FiguresAsync(host);

        figures.ComputedAt.ShouldBe(_time.GetUtcNow());
        figures.Currencies.ShouldBe(
        [
            // 50,000 + 250,000 / 12 + 12,500 without tax; four paid at once: 60,000 + 300,000 + 15,000 + 30,000.
            new CurrencyFigures("TRY", Mrr: 83_333, Active: 3, Trialing: 1, NewThisMonth: 5, ChurnedThisMonth: 1, Revenue: 405_000, FailedPayments: 1, DunningStarted: 1, Recovered: 0, AwaitingReconciliation: 0),
            new CurrencyFigures("USD", Mrr: 6_000, Active: 1, Trialing: 0, NewThisMonth: 1, ChurnedThisMonth: 0, Revenue: 0, FailedPayments: 0, DunningStarted: 0, Recovered: 0, AwaitingReconciliation: 0),
        ]);
        (figures.Currencies[0].RecoveryRate, figures.Currencies[1].RecoveryRate).ShouldBe((0m, null), "the declined first payment waits to be recovered; nothing failed in dollars");

        // Within five minutes the figures are the ones kept; then they are computed again. An invoice that was in dunning and is paid counts as recovered.
        await SubscribeAsync(api, "team-monthly-try");
        await host.ExecuteSqlAsync($"UPDATE tahsil.invoices SET dunning_started_at = {_time.GetUtcNow()} WHERE currency = 'TRY' AND subscription_id = {left.Id}");
        _time.Advance(TimeSpan.FromMinutes(4));
        var kept = await FiguresAsync(host);
        _time.Advance(TimeSpan.FromMinutes(1));
        var fresh = await FiguresAsync(host);

        kept.ShouldBeSameAs(figures);
        (fresh.Currencies[0].Mrr, fresh.Currencies[0].Active, fresh.Currencies[0].DunningStarted, fresh.Currencies[0].Recovered, fresh.Currencies[0].RecoveryRate).ShouldBe((108_333L, 4, 2, 1, 0.5m));

        await using var admin = new AdminComponents(host);
        var home = admin.Render<Home>();
        home.WaitForAssertion(() => home.FindAll("#figures tbody tr").Count.ShouldBe(2));
        var rows = home.FindAll("#figures tbody tr");
        (rows[0].QuerySelector(".mrr")!.TextContent, rows[0].QuerySelector(".revenue")!.TextContent, rows[0].QuerySelector(".recovery")!.TextContent).ShouldBe(
            ("1,083.33 TRY", "4,350.00 TRY", "1 of 2 invoices (50%)"));
        (rows[1].QuerySelector(".mrr")!.TextContent, rows[1].QuerySelector(".recovery")!.TextContent).ShouldBe(("60.00 USD", "–"));
    }

    private static async Task<DashboardFigures> FiguresAsync(WebApplicationFactory<Program> host)
    {
        await using var admin = new AdminComponents(host, AdminRoles.Viewer, withProviders: false);
        var result = await admin.Services.GetRequiredService<AdminOperations>().RunAsync<GetDashboardHandler, Domain.Common.Result<DashboardFigures>>(handler => handler.HandleAsync(CancellationToken));
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    /// <summary>A company with a card that subscribes to one price, charged at once unless the price has a trial.</summary>
    private static async Task<SubscriptionResponse> SubscribeAsync(HttpClient api, string price, int quantity = 1, string? coupon = null)
    {
        var customer = await CompanyAsync(api);
        await SaveCardAsync(api, customer, "4242424242424242");
        return await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new SubscriptionItemRequest { Price = price, Quantity = quantity }], Coupon = coupon });
    }

    private static async Task ProvisionAsync(HttpClient api)
    {
        using var provisioned = await api.PostJsonAsync("/api/v1/provisioning", new ProvisioningDocument
        {
            Products =
            [
                new ProvisionedProduct
                {
                    Key = "team",
                    Name = "Team",
                    Prices =
                    [
                        new ProvisionedPrice
                        {
                            Key = "team-monthly-usd", Name = "Monthly", Currency = "USD", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Licensed, UnitAmount = 2_000,
                        },
                    ],
                },
            ],
            Coupons = [new ProvisionedCoupon { Key = "half", Name = "Half", PercentOff = 50, Duration = CouponDuration.Forever }],
        });
        provisioned.StatusCode.ShouldBe(HttpStatusCode.OK, await provisioned.Content.ReadAsStringAsync(CancellationToken));
    }
}
