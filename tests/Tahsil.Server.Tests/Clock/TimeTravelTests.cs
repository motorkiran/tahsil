using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Tahsil.Application.TestClock;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;

namespace Tahsil.Server.Tests.Clock;

/// <summary>Time travel in Test mode (ENG-21 to ENG-23).</summary>
public sealed class TimeTravelTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersRead, TahsilPermissions.CustomersWrite, TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite,
        TahsilPermissions.PaymentsRead, TahsilPermissions.TestWrite, TahsilPermissions.ConnectionsWrite, TahsilPermissions.AuditRead,
    ];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "ENG-23")]
    [Trait("Requirement", "ENG-22")]
    public async Task AdvancingAMonth_ProcessesEachDueItemAtItsOwnInstant_InOrder()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await WithoutSimulatorNotificationsAsync(client);
        var start = (await GetAsync<TestClockResponse>(client, "/api/v1/test/clock")).Now;
        var customer = await CustomerAsync(client);

        // A card that answers after the charge's timeout, and another one that expires at the end of this month.
        var timesOut = await SaveTestCardAsync(client, customer, "4000000000001000", 12, start.Year + 1);
        var expiring = await SaveTestCardAsync(client, customer, "4242424242424242", start.Month, start.Year);
        var invoice = await FinalizedInvoiceAsync(client, customer);

        using var travel = await client.PostAsJsonAsync(new Uri("/api/v1/test/clock", UriKind.Relative), new AdvanceClockRequest { AdvanceBy = "P1M" }, ApiClients.Json, CancellationToken);
        var summary = await ReadAsync<TimeTravelResponse>(travel);
        var payment = (await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={invoice}")).Items.ShouldHaveSingleItem();
        var card = await GetAsync<PaymentMethodResponse>(client, $"/api/v1/payment-methods/{expiring.Id}");
        var clock = await GetAsync<TestClockResponse>(client, "/api/v1/test/clock");

        // The worker charged the default card at once; the charge got no answer and reconciliation found it 2 minutes later.
        (payment.Status, payment.PaymentMethodId).ShouldBe((PaymentStatus.Succeeded, timesOut.Id));
        (payment.SucceededAt!.Value - payment.CreatedAt).ShouldBeInRange(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).Status.ShouldBe(InvoiceStatus.Paid);

        // The other card expired at the end of its month, not at the end of the travel.
        card.Status.ShouldBe(PaymentMethodStatus.Expired);
        card.UpdatedAt.ShouldBeInRange(card.ExpiresAt!.Value, card.ExpiresAt.Value.AddMinutes(1));
        payment.SucceededAt.Value.ShouldBeLessThan(card.UpdatedAt);

        summary.To.ShouldBeInRange(summary.From.AddMonths(1).AddSeconds(-1), summary.From.AddMonths(1));
        summary.Steps.ShouldBeGreaterThanOrEqualTo(2);
        (summary.PaymentsAttempted, summary.PaymentsSucceeded, summary.PaymentsFailed).ShouldBe((1, 1, 0));
        clock.Now.ShouldBeGreaterThanOrEqualTo(summary.To);
        clock.OffsetSeconds.ShouldBeGreaterThan(27 * 24 * 3600);
    }

    [Fact]
    [Trait("Requirement", "ENG-23")]
    [Trait("Requirement", "SUB-11")]
    public async Task AYearInOneCall_RenewsAMonthlySubscriptionTwelveTimes_EachAtItsOwnInstant()
    {
        await using var host = Host();
        using var client = await Subscriptions.SubscriptionScenarios.ClientAsync(host);
        var subscription = await Subscriptions.SubscriptionScenarios.SendingInvoicesAsync(client, await CustomerAsync(client));

        using var travel = await client.PostAsJsonAsync(new Uri("/api/v1/test/clock", UriKind.Relative), new AdvanceClockRequest { AdvanceBy = "P1Y" }, ApiClients.Json, CancellationToken);
        var summary = await ReadAsync<TimeTravelResponse>(travel);

        var renewals = await host.QueryAsync<Renewal>($"""
            SELECT period_start, created_at FROM tahsil.invoices WHERE billing_reason = 'subscription_cycle' ORDER BY period_start
            """);
        renewals.Select(renewal => renewal.PeriodStart).ShouldBe(Enumerable.Range(1, 12).Select(month => subscription.CurrentPeriodStart.AddMonths(month)));
        renewals.ShouldAllBe(renewal => renewal.CreatedAt >= renewal.PeriodStart && renewal.CreatedAt < renewal.PeriodStart.AddMinutes(1));
        summary.InvoicesCreated.ShouldBe(12);
    }

    [Theory]
    [Trait("Requirement", "ENG-22")]
    [InlineData("P")]
    [InlineData("P2Y")]
    [InlineData("1 month")]
    [InlineData("-P1D")]
    public async Task TheClock_OnlyMovesForward_AndAtMost400Days(string duration)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/test/clock", UriKind.Relative), new AdvanceClockRequest { AdvanceBy = duration }, ApiClients.Json, CancellationToken);
        using var backwards = await client.PostAsJsonAsync(
            new Uri("/api/v1/test/clock", UriKind.Relative), new AdvanceClockRequest { AdvanceTo = DateTimeOffset.UnixEpoch }, ApiClients.Json, CancellationToken);

        (await response.ProblemCodeAsync()).ShouldBe("test_clock.invalid");
        (await backwards.ProblemCodeAsync()).ShouldBe("test_clock.invalid");
    }

    [Theory]
    [Trait("Requirement", "ENG-22")]
    [InlineData("2026-01-31T10:00:00Z", "P1M", "2026-02-28T10:00:00Z")]
    [InlineData("2026-10-10T10:00:00Z", "P1Y2M10DT2H30M", "2027-12-20T12:30:00Z")]
    [InlineData("2026-10-10T10:00:00Z", "PT36H", "2026-10-11T22:00:00Z")]
    [InlineData("2026-10-10T10:00:00Z", "P2W", "2026-10-24T10:00:00Z")]
    public void Durations_AreCalendarStepsThenTime(string from, string duration, string expected)
    {
        IsoDuration.Add(DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture), duration, TimeZoneInfo.Utc)
            .ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Requirement", "ENG-22")]
    public void Months_FollowTheBillingTimeZone()
    {
        var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

        // 31 January 22:00 UTC is already 1 February in Istanbul; a month later is 1 March there.
        IsoDuration.Add(new DateTimeOffset(2026, 1, 31, 22, 0, 0, TimeSpan.Zero), "P1M", istanbul)
            .ShouldBe(new DateTimeOffset(2026, 2, 28, 22, 0, 0, TimeSpan.Zero));
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("clock"),

            // Time travel runs the workers itself.
            ["Tahsil:Workers:Enabled"] = "false",
        });

    private static async Task WithoutSimulatorNotificationsAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { notifications = false } }, options: ApiClients.Json),
        };
        (await client.SendAsync(request, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<PaymentMethodResponse> SaveTestCardAsync(HttpClient client, Guid customer, string card, int expMonth, int expYear)
    {
        using var response = await client.PostJsonAsync("/api/v1/test/simulator/payment-methods", new CreateSimulatorPaymentMethodRequest
        {
            Customer = customer,
            Card = card,
            ExpMonth = expMonth,
            ExpYear = expYear,
        });
        return await ReadAsync<PaymentMethodResponse>(response, HttpStatusCode.Created);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path) => await ReadAsync<T>(await client.GetPathAsync(path));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(CancellationToken));
            return await response.ReadAsync<T>();
        }
    }

    private sealed record Renewal(DateTimeOffset PeriodStart, DateTimeOffset CreatedAt);
}
