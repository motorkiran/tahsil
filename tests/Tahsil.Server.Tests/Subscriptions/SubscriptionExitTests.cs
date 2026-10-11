using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>
/// The M4 exit scenario: a subscription anchored at a month's end lives through twelve months with an upgrade, a
/// downgrade and a cancellation, and every invoice is compared line by line with values written here; trials with and
/// without cards convert or end as specified.
/// </summary>
[Trait("Suite", "Exit")]
public sealed class SubscriptionExitTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SUB-11")]
    [Trait("Requirement", "MNY-27")]
    [Trait("Requirement", "MNY-30")]
    [Trait("Requirement", "SUB-19")]
    [Trait("Requirement", "SUB-28")]
    public async Task ASubscription_LivesTwelveMonths_WithAnUpgradeADowngradeAndACancellation_BilledToTheMinorUnit()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");

        // January 31: two seats, paid at once.
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try", Quantity = 2 }] });
        subscription.Status.ShouldBe(SubscriptionStatus.Active);

        // Renewals keep the month's end: February 28, March 31.
        await RenewAsync(host, At(2, 28));
        await RenewAsync(host, At(3, 31));

        // April 15, halfway through [March 31, April 30): four seats, the difference invoiced at once.
        await AdvanceToAsync(host, At(4, 15));
        await PatchAsync(client, subscription.Id, new { items = Seats(4) });

        await RenewAsync(host, At(4, 30));
        await RenewAsync(host, At(5, 31));

        // June 10: back to one seat at the period end.
        await AdvanceToAsync(host, At(6, 10));
        await PatchAsync(client, subscription.Id, new { items = Seats(1), effective = "period_end" });
        foreach (var renewal in new[] { At(6, 30), At(7, 31), At(8, 31), At(9, 30), At(10, 31), At(11, 30), At(12, 31) })
        {
            await RenewAsync(host, renewal);
        }

        // January 10, 2027: canceled at the end of the twelfth month.
        await AdvanceToAsync(host, new DateTimeOffset(2027, 1, 10, 10, 0, 0, TimeSpan.Zero));
        using (var canceled = await client.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new { at = "period_end", reason = "requested_by_customer" }))
        {
            canceled.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await RenewAsync(host, new DateTimeOffset(2027, 1, 31, 10, 0, 0, TimeSpan.Zero));

        var ended = await GetSubscriptionAsync(client, subscription.Id);
        (ended.Status, ended.EndedAt, ended.CancellationReason).ShouldBe((SubscriptionStatus.Canceled, new DateTimeOffset(2027, 1, 31, 10, 0, 0, TimeSpan.Zero), CancellationReason.RequestedByCustomer));
        var invoices = (await GetAsync<Page<InvoiceResponse>>(client, $"/api/v1/invoices?subscription={subscription.Id}&limit=50")).Items.OrderBy(invoice => invoice.CreatedAt).ToList();
        invoices.Select(Summary).ShouldBe(
        [
            Expected(BillingReason.SubscriptionCreate, At(1, 31), At(2, 28), Seat(2)),
            Expected(BillingReason.SubscriptionCycle, At(2, 28), At(3, 31), Seat(2)),
            Expected(BillingReason.SubscriptionCycle, At(3, 31), At(4, 30), Seat(2)),
            Expected(
                BillingReason.SubscriptionUpdate,
                At(4, 15),
                At(4, 30),
                (InvoiceLineType.Proration, "15 Nis 2026 sonrası kullanılmayan süre: Team (Aylık) × 2", -30_000, -5_000),
                (InvoiceLineType.Proration, "15 Nis 2026 sonrası kalan süre: Team (Aylık) × 4", 60_000, 10_000)),
            Expected(BillingReason.SubscriptionCycle, At(4, 30), At(5, 31), Seat(4)),
            Expected(BillingReason.SubscriptionCycle, At(5, 31), At(6, 30), Seat(4)),
            Expected(BillingReason.SubscriptionCycle, At(6, 30), At(7, 31), Seat(1)),
            Expected(BillingReason.SubscriptionCycle, At(7, 31), At(8, 31), Seat(1)),
            Expected(BillingReason.SubscriptionCycle, At(8, 31), At(9, 30), Seat(1)),
            Expected(BillingReason.SubscriptionCycle, At(9, 30), At(10, 31), Seat(1)),
            Expected(BillingReason.SubscriptionCycle, At(10, 31), At(11, 30), Seat(1)),
            Expected(BillingReason.SubscriptionCycle, At(11, 30), At(12, 31), Seat(1)),
            Expected(BillingReason.SubscriptionCycle, At(12, 31), new DateTimeOffset(2027, 1, 31, 10, 0, 0, TimeSpan.Zero), Seat(1)),
        ]);
        invoices.ShouldAllBe(invoice => invoice.Status == InvoiceStatus.Paid && invoice.AmountPaid == invoice.Total);
        invoices.Select(invoice => invoice.Number).ShouldBe(Enumerable.Range(1, 13).Select(number => $"INV-2026-{number:D6}"), "twelve periods and the upgrade");
    }

    [Fact]
    [Trait("Requirement", "SUB-14")]
    public async Task ATrialWithACard_ConvertsAtItsEnd_AndThePaidPeriodFollows()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var trial = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });
        var trialEnd = Start.AddDays(14);

        await RenewAsync(host, trialEnd);

        var converted = await GetSubscriptionAsync(client, trial.Id);
        (converted.Status, converted.CurrentPeriodStart, converted.CurrentPeriodEnd).ShouldBe((SubscriptionStatus.Active, trialEnd, trialEnd.AddMonths(1)));
        var first = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{converted.LatestInvoice!.Id}");
        Summary(first).ShouldBe(Expected(BillingReason.SubscriptionCycle, trialEnd, trialEnd.AddMonths(1), (InvoiceLineType.Subscription, "Pro (Aylık) × 1", 50_000, 8_333)));
        first.Status.ShouldBe(InvoiceStatus.Paid);
    }

    [Theory]
    [Trait("Requirement", "SUB-15")]
    [InlineData("cancel", SubscriptionStatus.Canceled, null)]
    [InlineData("create_invoice", SubscriptionStatus.PastDue, "payment_method_missing")]
    public async Task ATrialWithoutACard_EndsOrIsInvoiced_AsAgreed(string behavior, SubscriptionStatus status, string? failure)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        var trial = await CreateAsync(client, new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new() { Price = "pro-monthly-try" }],
            TrialEndBehavior = behavior == "cancel" ? TrialEndBehavior.Cancel : TrialEndBehavior.CreateInvoice,
        });

        await RenewAsync(host, Start.AddDays(14));

        var ended = await GetSubscriptionAsync(client, trial.Id);
        ended.Status.ShouldBe(status);
        if (failure is null)
        {
            (ended.CancellationReason, ended.LatestInvoice).ShouldBe((CancellationReason.TrialWithoutPaymentMethod, null));
        }
        else
        {
            var payment = (await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={ended.LatestInvoice!.Id}")).Items.ShouldHaveSingleItem();
            (payment.Status, payment.FailureCode).ShouldBe((PaymentStatus.Failed, failure));
        }
    }

    private static DateTimeOffset At(int month, int day) => new(2026, month, day, 10, 0, 0, TimeSpan.Zero);

    private static object[] Seats(int quantity) => [new { price = "team-monthly-try", quantity }];

    private static (InvoiceLineType, string, long, long) Seat(int quantity) =>
        (InvoiceLineType.Subscription, $"Team (Aylık) × {quantity}", 30_000L * quantity, 5_000L * quantity);

    private static string Expected(BillingReason reason, DateTimeOffset start, DateTimeOffset end, params (InvoiceLineType Type, string Description, long Amount, long Tax)[] lines) =>
        $"{reason} {start:yyyy-MM-dd} {end:yyyy-MM-dd} total {lines.Sum(line => line.Amount)}: "
        + string.Join("; ", lines.Select(line => $"{line.Type} '{line.Description}' {line.Amount} tax {line.Tax}"));

    private static string Summary(InvoiceResponse invoice) =>
        $"{invoice.BillingReason} {invoice.PeriodStart:yyyy-MM-dd} {invoice.PeriodEnd:yyyy-MM-dd} total {invoice.Total}: "
        + string.Join("; ", invoice.Lines.Select(line => $"{line.Type} '{line.Description}' {line.Amount} tax {line.TaxAmount}"));

    private static async Task PatchAsync(HttpClient client, Guid id, object body)
    {
        using var response = await client.PatchJsonAsync($"/api/v1/subscriptions/{id}", body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private async Task AdvanceToAsync(WebApplicationFactory<Program> host, DateTimeOffset instant)
    {
        _time.Advance(instant - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
    }

    /// <summary>Moves to a renewal, then past its draft period, so the cycle invoice is finalized and collected.</summary>
    private async Task RenewAsync(WebApplicationFactory<Program> host, DateTimeOffset renewal)
    {
        await AdvanceToAsync(host, renewal);
        await AdvanceToAsync(host, renewal.AddHours(1));
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("subscription_exit"), _time);
}
