using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>
/// Dunning (DUN §11) on a fake clock with the simulator: a monthly subscription renews on May 1 at midnight, and its
/// payment fails at 01:00, once the draft period is over, so retries come on May 2, 4, 6 and 8 at 01:00.
/// </summary>
public sealed class DunningTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstFailure = new(2026, 5, 1, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "DUN-2")]
    [Trait("Requirement", "DUN-5")]
    [Trait("Requirement", "DUN-10")]
    public async Task ARetryableDecline_IsRetriedOnTheSchedule_AndRecovers()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (subscription, card) = await PayingAsync(client);
        await SetBehaviorAsync(client, card, "insufficient_funds");

        await FailRenewalAsync(host);
        var pastDue = await GetSubscriptionAsync(client, subscription.Id);
        var invoice = pastDue.LatestInvoice!.Id;
        (pastDue.Status, pastDue.PastDueSince).ShouldBe((SubscriptionStatus.PastDue, FirstFailure));
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).NextPaymentAttemptAt.ShouldBe(FirstFailure.AddDays(1));

        await AdvanceToAsync(host, FirstFailure.AddDays(1));
        await AdvanceToAsync(host, FirstFailure.AddDays(3));
        await SetBehaviorAsync(client, card, "succeed");
        await AdvanceToAsync(host, FirstFailure.AddDays(5));

        var recovered = await GetSubscriptionAsync(client, subscription.Id);
        (recovered.Status, recovered.PastDueSince).ShouldBe((SubscriptionStatus.Active, null));
        (await PaymentTimesAsync(client, invoice)).ShouldBe(
            [(FirstFailure, "insufficient_funds"), (FirstFailure.AddDays(1), "insufficient_funds"), (FirstFailure.AddDays(3), "insufficient_funds"), (FirstFailure.AddDays(5), null)]);
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).Status.ShouldBe(InvoiceStatus.Paid);
    }

    [Theory]
    [Trait("Requirement", "DUN-6")]
    [InlineData("leave_open", InvoiceStatus.Open)]
    [InlineData("mark_uncollectible", InvoiceStatus.Uncollectible)]
    [InlineData("void", InvoiceStatus.Void)]
    public async Task WhenEveryRetryFails_TheFinalActionCancels_AndHandlesTheInvoiceAsConfigured(string action, InvoiceStatus status)
    {
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Dunning:InvoiceAction"] = action });
        using var client = await ClientAsync(host);
        var (subscription, card) = await PayingAsync(client);
        await SetBehaviorAsync(client, card, "card_declined");

        await FailRenewalAsync(host);
        foreach (var day in new[] { 1, 3, 5 })
        {
            await AdvanceToAsync(host, FirstFailure.AddDays(day));
        }

        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.PastDue, "not yet: the last retry is on day 7");
        await AdvanceToAsync(host, FirstFailure.AddDays(7));

        var canceled = await GetSubscriptionAsync(client, subscription.Id);
        (canceled.Status, canceled.CancellationReason, canceled.EndedAt).ShouldBe((SubscriptionStatus.Canceled, CancellationReason.PaymentFailed, FirstFailure.AddDays(7)));
        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{canceled.LatestInvoice!.Id}");
        (invoice.Status, invoice.AttemptCount, invoice.NextPaymentAttemptAt).ShouldBe((status, 5, null));
    }

    [Fact]
    [Trait("Requirement", "DUN-3")]
    [Trait("Requirement", "DUN-8")]
    public async Task ACardThatCannotBeUsed_IsNotRetried_UntilTheCustomerGivesAnother()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (subscription, card) = await PayingAsync(client);
        await SetBehaviorAsync(client, card, "expired_card");

        await FailRenewalAsync(host);
        var invoice = (await GetSubscriptionAsync(client, subscription.Id)).LatestInvoice!.Id;
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).NextPaymentAttemptAt.ShouldBeNull();
        await AdvanceToAsync(host, FirstFailure.AddDays(2));
        var another = await SaveCardAsync(client, subscription.CustomerId, "4000000000007726");
        using (var made = await client.PostJsonAsync($"/api/v1/customers/{subscription.CustomerId}/default-payment-method", new SetDefaultPaymentMethodRequest { PaymentMethod = another }))
        {
            made.EnsureSuccessStatusCode();
        }

        await host.RunWorkersUntilIdleAsync();

        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Active);
        (await PaymentTimesAsync(client, invoice)).ShouldBe([(FirstFailure, "expired_card"), (FirstFailure.AddDays(2), null)]);
    }

    [Fact]
    [Trait("Requirement", "DUN-4")]
    public async Task APaymentNeedingTheCustomer_IsPaidOnTheInvoicePage_With3DSecure()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (subscription, card) = await PayingAsync(client);
        await SetBehaviorAsync(client, card, "authentication_required");

        await FailRenewalAsync(host);
        var invoice = (await GetSubscriptionAsync(client, subscription.Id)).LatestInvoice!.Id;
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).NextPaymentAttemptAt.ShouldBeNull();
        await PayOnPageAsync(host, invoice, "4000000000003220");

        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Active);
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE action = 'invoice.action_required'")).ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Requirement", "DUN-7")]
    public async Task TheMerchantsFailures_NeverCountAgainstTheCustomer_AndReachTheOperatorAfterADay()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (subscription, _) = await PayingAsync(client);
        await EnableConnectionAsync(client, enabled: false);

        await FailRenewalAsync(host);
        var invoice = (await GetSubscriptionAsync(client, subscription.Id)).LatestInvoice!.Id;
        for (var hour = 1; hour <= 72; hour++)
        {
            await AdvanceToAsync(host, FirstFailure.AddHours(hour));
        }

        var stillActive = await GetSubscriptionAsync(client, subscription.Id);
        var attention = await host.QueryAsync<DateTimeOffset>($"SELECT raised_at AS \"Value\" FROM tahsil.attention_items WHERE kind = 'merchant_failures'");
        await EnableConnectionAsync(client, enabled: true);
        await AdvanceToAsync(host, FirstFailure.AddDays(4));

        stillActive.Status.ShouldBe(SubscriptionStatus.Active);
        attention.ShouldHaveSingleItem().ShouldBeGreaterThanOrEqualTo(FirstFailure.AddHours(24));
        var payments = await PaymentTimesAsync(client, invoice);
        payments.Take(payments.Count - 1).ShouldAllBe(payment => payment.FailureCode == "provider_unavailable");
        payments[^1].FailureCode.ShouldBeNull();
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).Status.ShouldBe(InvoiceStatus.Paid);
    }

    [Fact]
    [Trait("Requirement", "DUN-9")]
    public async Task SeveralOpenInvoices_AreCollectedOldestFirst()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        var card = await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "growth-daily-try" }] });
        await SetBehaviorAsync(client, card, "expired_card");

        foreach (var day in new[] { 1, 2 })
        {
            await AdvanceToAsync(host, April.AddDays(day));
            await AdvanceToAsync(host, April.AddDays(day).AddHours(1));
        }
        var waiting = (await GetAsync<Page<InvoiceResponse>>(client, $"/api/v1/invoices?subscription={subscription.Id}&status=open")).Items.OrderBy(invoice => invoice.CreatedAt).ToList();
        var another = await SaveCardAsync(client, customer, "4000000000007726");
        using (var made = await client.PostJsonAsync($"/api/v1/customers/{customer}/default-payment-method", new SetDefaultPaymentMethodRequest { PaymentMethod = another }))
        {
            made.EnsureSuccessStatusCode();
        }

        await host.RunWorkersUntilIdleAsync();

        waiting.Select(invoice => invoice.NextPaymentAttemptAt).ShouldBe([null, null], "the first waits for a card, the second for the first");
        waiting[1].AttemptCount.ShouldBe(0);
        var paid = await host.QueryAsync<Guid>($"""
            SELECT invoice_id AS "Value" FROM tahsil.payments WHERE status = 'succeeded' AND payment_method_id = {another} ORDER BY succeeded_at, created_at
            """);
        paid.ShouldBe([waiting[0].Id, waiting[1].Id]);
        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Active);
    }

    /// <summary>A company's monthly Team subscription, paid on April 1 with the returned card.</summary>
    private static async Task<(SubscriptionResponse Subscription, Guid Card)> PayingAsync(HttpClient client)
    {
        var customer = await CompanyAsync(client);
        var card = await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        return (subscription, card);
    }

    private static async Task<List<(DateTimeOffset At, string? FailureCode)>> PaymentTimesAsync(HttpClient client, Guid invoice) =>
        [.. (await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={invoice}")).Items.OrderBy(payment => payment.CreatedAt).Select(payment => (payment.CreatedAt, payment.FailureCode))];

    private static async Task EnableConnectionAsync(HttpClient client, bool enabled)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { enabled }, options: ApiClients.Json),
        };
        using var response = await client.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>The May 1 renewal at midnight, and its first payment an hour later.</summary>
    private async Task FailRenewalAsync(WebApplicationFactory<Program> host)
    {
        await AdvanceToAsync(host, FirstFailure.AddHours(-1));
        await AdvanceToAsync(host, FirstFailure);
    }

    private async Task AdvanceToAsync(WebApplicationFactory<Program> host, DateTimeOffset instant)
    {
        if (instant > _time.GetUtcNow())
        {
            _time.Advance(instant - _time.GetUtcNow());
        }

        await host.RunWorkersUntilIdleAsync();
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("dunning"), _time, settings: settings);
}
