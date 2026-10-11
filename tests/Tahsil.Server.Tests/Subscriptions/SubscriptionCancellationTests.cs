using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>Cancellation in each mode with each refund option, and resume (SUB-28 to SUB-33), in April 2026.</summary>
public sealed class SubscriptionCancellationTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SUB-28")]
    [Trait("Requirement", "SUB-33")]
    public async Task AtThePeriodEnd_ItIsScheduled_CanBeResumed_AndEndsWithThePeriod()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client);
        var end = April.AddMonths(1);

        var scheduled = await CancelAsync(client, subscription.Id, new { reason = "requested_by_customer", feedback = "too_expensive", comment = "Back next year" });
        var resumed = await PostAsync<SubscriptionResponse>(client, $"/api/v1/subscriptions/{subscription.Id}/resume");
        await CancelAsync(client, subscription.Id, new { at = "period_end" });
        _time.Advance(TimeSpan.FromDays(31));
        await host.RunWorkersUntilIdleAsync();

        (scheduled.Status, scheduled.CancelAtPeriodEnd, scheduled.CancelAt, scheduled.CancellationFeedback, scheduled.CancellationComment)
            .ShouldBe((SubscriptionStatus.Active, true, end, CancellationFeedback.TooExpensive, "Back next year"));
        (resumed.CancelAt, resumed.CancelAtPeriodEnd, resumed.CanceledAt).ShouldBe((null, false, null));
        var ended = await GetSubscriptionAsync(client, subscription.Id);
        (ended.Status, ended.EndedAt, ended.CancellationReason).ShouldBe((SubscriptionStatus.Canceled, end, CancellationReason.RequestedByMerchant));
        using var tooLate = await client.PostPathAsync($"/api/v1/subscriptions/{subscription.Id}/resume");
        (await tooLate.ProblemCodeAsync()).ShouldBe("subscription.not_resumable");
    }

    [Theory]
    [Trait("Requirement", "SUB-30")]
    [InlineData("2026-04-20T12:00:00Z", null)]
    [InlineData("2026-05-01T00:00:00Z", null)]
    [InlineData("2026-05-02T00:00:00Z", "subscription.invalid_cancel_at")]
    [InlineData("2026-03-31T00:00:00Z", "subscription.invalid_cancel_at")]
    [InlineData("next week", "subscription.invalid")]
    public async Task AtATime_ItMustFallWithinThePeriod(string at, string? refused)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client);

        using var response = await client.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new { at });

        if (refused is null)
        {
            (await response.ReadAsync<SubscriptionResponse>()).CancelAt.ShouldBe(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            (await response.ProblemCodeAsync()).ShouldBe(refused);
        }
    }

    [Theory]
    [Trait("Requirement", "SUB-29")]
    [Trait("Requirement", "SUB-31")]
    [InlineData("none", 0, new long[0])]
    [InlineData("prorated", 0, new[] { 45_000L })]
    [InlineData("prorated", 60_000, new[] { 30_000L })]
    [InlineData("last_payment", 0, new[] { 90_000L })]
    public async Task Now_RefundsAsAsked_CappedByWhatCanStillBeRefunded(string refund, long refundedBefore, long[] refunds)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client, quantity: 3);
        var payment = (await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={subscription.LatestInvoice!.Id}")).Items.Single();
        if (refundedBefore > 0)
        {
            (await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment.Id, Amount = refundedBefore })).EnsureSuccessStatusCode().Dispose();
        }

        _time.Advance(TimeSpan.FromDays(15));
        var canceled = await CancelAsync(client, subscription.Id, new { at = "now", refund, reason = "requested_by_customer" });

        (canceled.Status, canceled.EndedAt, canceled.CancellationReason).ShouldBe((SubscriptionStatus.Canceled, April.AddDays(15), CancellationReason.RequestedByCustomer));
        (await host.QueryAsync<long>($"""
            SELECT amount AS "Value" FROM tahsil.refunds WHERE reason = 'subscription_canceled' AND status = 'succeeded' ORDER BY amount
            """)).ShouldBe(refunds);
    }

    [Theory]
    [Trait("Requirement", "SUB-29")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Now_PendingItemsAreInvoiced_OrDeleted(bool invoiceNow)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client);
        using (var item = await client.PostJsonAsync("/api/v1/invoice-items", new CreateInvoiceItemRequest
        {
            Customer = subscription.CustomerId,
            Subscription = subscription.Id,
            Currency = "TRY",
            Description = "Extra storage",
            UnitAmount = 5_000,
        }))
        {
            item.EnsureSuccessStatusCode();
        }

        var canceled = await CancelAsync(client, subscription.Id, new { at = "now", invoiceNow });
        await host.RunWorkersUntilIdleAsync();

        (await host.QueryAsync<long>($"SELECT count(*) AS \"Value\" FROM tahsil.pending_invoice_items")).ShouldBe([invoiceNow ? 1L : 0L]);
        if (invoiceNow)
        {
            var final = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{canceled.LatestInvoice!.Id}");
            (final.BillingReason, final.Total, final.Status).ShouldBe((BillingReason.SubscriptionCancel, 5_000L, InvoiceStatus.Paid));
        }
        else
        {
            canceled.LatestInvoice!.Id.ShouldBe(subscription.LatestInvoice!.Id);
        }
    }

    [Fact]
    [Trait("Requirement", "SUB-31")]
    public async Task Refunds_NeedTheRefundPermission()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client);
        using var limited = await host.CreateClientWithAsync(TahsilPermissions.SubscriptionsWrite, TahsilPermissions.SubscriptionsRead);

        using var refused = await limited.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new { at = "now", refund = "prorated" });
        using var scheduled = await limited.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new { at = "period_end", refund = "prorated" });
        using var allowed = await limited.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new { at = "now" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await scheduled.ProblemCodeAsync()).ShouldBe("auth.forbidden");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A company's monthly Team subscription paid with a saved card at the start of April.</summary>
    private static async Task<SubscriptionResponse> PayingCompanyAsync(HttpClient client, int quantity = 1)
    {
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        return await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try", Quantity = quantity }] });
    }

    private static async Task<SubscriptionResponse> CancelAsync(HttpClient client, Guid id, object body)
    {
        using var response = await client.PostJsonAsync($"/api/v1/subscriptions/{id}/cancel", body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<SubscriptionResponse>();
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path)
    {
        using var response = await client.PostPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("cancellations"), _time);
}
