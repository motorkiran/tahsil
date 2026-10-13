using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using static Tahsil.Server.Tests.Usage.UsageScenarios;

namespace Tahsil.Server.Tests.Usage;

/// <summary>
/// The M10 exit scenario: a subscription with a base fee and tiered, metered API calls lives through three periods on
/// the simulator. The application reports usage in batches and retries one; each renewal bills the period that ended,
/// with the events reported until its invoice is final; the card pays every invoice; the subscription ends with a
/// final invoice of its last usage. Every invoice is compared with values written here.
/// </summary>
[Trait("Suite", "Exit")]
public sealed class UsageExitTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "USG-2")]
    [Trait("Requirement", "USG-5")]
    [Trait("Requirement", "USG-9")]
    [Trait("Requirement", "USG-10")]
    [Trait("Requirement", "USG-11")]
    [Trait("Requirement", "MNY-10")]
    public async Task AMeteredSubscription_BillsTieredUsageAtTheEndOfEachPeriod_ToTheCard()
    {
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("usage_exit"), _time);
        using var api = await ApiAsync(host);
        await ProvisionAsync(api);
        var customer = await CustomerAsync(api);
        await SaveCardAsync(api, customer, "4242424242424242");
        var start = _time.GetUtcNow();
        var (second, third, end) = (start.AddMonths(1), start.AddMonths(2), start.AddMonths(3));

        // 1 October: the base fee is charged at once; usage is billed when the period has ended.
        var subscription = await CreateAsync(api, new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new SubscriptionItemRequest { Price = "platform-base-try" }, new SubscriptionItemRequest { Price = "platform-calls-try" }],
        });
        subscription.Status.ShouldBe(SubscriptionStatus.Active);

        // The first period: 36,000 calls in two batches, the second of which the application sends twice.
        _time.Advance(TimeSpan.FromDays(9));
        (await ReportAsync(api, Event("oct-1", customer, "api_calls", 6_000))).Accepted.ShouldBe(1);
        _time.Advance(TimeSpan.FromDays(9));
        object[] batch = [Event("oct-2", customer, "api_calls", 10_000), Event("oct-3", customer, "api_calls", 20_000)];
        (await ReportAsync(api, batch)).Accepted.ShouldBe(2);
        var retried = await ReportAsync(api, batch);
        (retried.Accepted, retried.Rejected.Count).ShouldBe((0, 0));
        retried.Duplicates.ShouldBe(["oct-2", "oct-3"]);

        // 1 November: the renewal's draft. 4,000 calls of October arrive late, before it is final, and count.
        await AdvanceToAsync(host, second);
        (await ReportAsync(api, Event("oct-late", customer, "api_calls", 4_000, second.AddMinutes(-5)))).Accepted.ShouldBe(1);
        await AdvanceToAsync(host, second.AddHours(1));
        var tooLate = await ReportAsync(api, Event("oct-too-late", customer, "api_calls", 1_000, second.AddMinutes(-5)));
        tooLate.Rejected.ShouldHaveSingleItem().Code.ShouldBe("usage_event.period_closed");

        // The second period: 150,000 calls reach the third tier.
        _time.Advance(TimeSpan.FromDays(10));
        (await ReportAsync(api, Event("nov-1", customer, "api_calls", 150_000))).Accepted.ShouldBe(1);
        await AdvanceToAsync(host, third);
        await AdvanceToAsync(host, third.AddHours(1));

        // The third period: the customer leaves at its end; what they used until then is billed on a final invoice.
        _time.Advance(TimeSpan.FromDays(10));
        (await ReportAsync(api, Event("dec-1", customer, "api_calls", 12_345))).Accepted.ShouldBe(1);
        using (var canceled = await api.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new CancelSubscriptionRequest { At = "period_end", Reason = CancellationReason.RequestedByCustomer }))
        {
            canceled.StatusCode.ShouldBe(HttpStatusCode.OK, await canceled.Content.ReadAsStringAsync(CancellationToken));
        }

        await AdvanceToAsync(host, end);
        await AdvanceToAsync(host, end.AddHours(1));

        var ended = await GetSubscriptionAsync(api, subscription.Id);
        (ended.Status, ended.EndedAt).ShouldBe((SubscriptionStatus.Canceled, end));
        var invoices = (await GetAsync<Page<InvoiceResponse>>(api, $"/api/v1/invoices?subscription={subscription.Id}")).Items.OrderBy(invoice => invoice.Number).ToList();
        invoices.Select(invoice => (invoice.BillingReason, invoice.Total, invoice.TaxTotal, Lines: string.Join(" | ", invoice.Lines.Select(line => $"{line.Type}: {line.Description} = {line.Amount}")))).ShouldBe(
        [
            (BillingReason.SubscriptionCreate, 20_000L, 3_333L, "Subscription: Platform (Base) × 1 = 20000"),

            // 40,000 calls: 10,000 free, 30,000 at 0.2 kuruş.
            (BillingReason.SubscriptionCycle, 26_000L, 4_333L, "Subscription: Platform (Base) × 1 = 20000 | Usage: API calls: 40,000 = 6000"),

            // 150,000 calls: 10,000 free, 90,000 at 0.2 kuruş, 50,000 at 0.1 kuruş.
            (BillingReason.SubscriptionCycle, 43_000L, 7_166L, "Subscription: Platform (Base) × 1 = 20000 | Usage: API calls: 150,000 = 23000"),

            // 12,345 calls: 10,000 free, 2,345 at 0.2 kuruş.
            (BillingReason.SubscriptionCancel, 469L, 78L, "Usage: API calls: 12,345 = 469"),
        ]);
        invoices.Select(invoice => invoice.Lines.Where(line => line.Type == InvoiceLineType.Usage).Select(line => (line.PeriodStart, line.PeriodEnd)).SingleOrDefault()).ShouldBe(
            [default, (start, second), (second, third), (third, end)]);
        invoices.ShouldAllBe(invoice => invoice.Status == InvoiceStatus.Paid && invoice.AmountPaid == invoice.Total);
        var payments = (await GetAsync<Page<PaymentResponse>>(api, $"/api/v1/payments?customer={customer}")).Items;
        payments.Select(payment => (payment.Status, payment.Amount)).ShouldBe(
            [(PaymentStatus.Succeeded, 20_000L), (PaymentStatus.Succeeded, 26_000L), (PaymentStatus.Succeeded, 43_000L), (PaymentStatus.Succeeded, 469L)], ignoreOrder: true);
    }

    /// <summary>A platform with a base fee of ₺200 and API calls that are free up to 10,000, then 0.2 kuruş each up to 100,000 and 0.1 kuruş beyond, KDV included.</summary>
    private static async Task ProvisionAsync(HttpClient api)
    {
        using var provisioned = await api.PostJsonAsync("/api/v1/provisioning", new ProvisioningDocument
        {
            TaxRates = [new ProvisionedTaxRate { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true }],
            Products =
            [
                new ProvisionedProduct
                {
                    Key = "platform",
                    Name = "Platform",
                    Prices =
                    [
                        new ProvisionedPrice
                        {
                            Key = "platform-base-try", Name = "Base", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Licensed, UnitAmount = 20_000, TaxRate = "kdv-20",
                        },
                        new ProvisionedPrice
                        {
                            Key = "platform-calls-try",
                            Currency = "TRY",
                            Type = PriceType.Recurring,
                            Interval = PriceInterval.Month,
                            IntervalCount = 1,
                            UsageType = UsageType.Metered,
                            Meter = "api_calls",
                            BillingScheme = BillingScheme.Tiered,
                            TiersMode = TiersMode.Graduated,
                            Tiers = [new PriceTier(10_000, UnitAmount: 0), new PriceTier(100_000, UnitAmountDecimal: 0.2m), new PriceTier(null, UnitAmountDecimal: 0.1m)],
                            TaxRate = "kdv-20",
                        },
                    ],
                },
            ],
        });
        provisioned.StatusCode.ShouldBe(HttpStatusCode.OK, await provisioned.Content.ReadAsStringAsync(CancellationToken));
    }

    private async Task AdvanceToAsync(WebApplicationFactory<Program> host, DateTimeOffset instant)
    {
        _time.Advance(instant - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
    }
}
