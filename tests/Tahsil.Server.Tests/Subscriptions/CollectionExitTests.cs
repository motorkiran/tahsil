using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Server.Tests.Notifications;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>
/// The M5 exit scenario, with email sent over SMTP to Mailpit: a failed renewal recovers after a card update, an
/// unrecovered one is canceled on schedule, the merchant's failures never cancel a customer, and a company pays its
/// invoice by bank transfer; each customer gets their emails once, in their language. Monthly subscriptions start on
/// April 1 and renew on May 1 at midnight, and the renewal's first payment comes at 01:00.
/// </summary>
[Trait("Suite", "Exit")]
public sealed class CollectionExitTests(TahsilServerFixture server, Mailpit mailpit) : IClassFixture<Mailpit>
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstFailure = new(2026, 5, 1, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "DUN-3")]
    [Trait("Requirement", "DUN-8")]
    [Trait("Requirement", "NTF-12")]
    public async Task AFailedRenewal_RecoversWhenTheCustomerGivesAnotherCard()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (customer, email) = await CustomerAsync(client, "tr");
        var card = await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await SubscribeAsync(host, client, customer);
        await SetBehaviorAsync(client, card, "expired_card");

        await FailRenewalAsync(host);
        var pastDue = await GetSubscriptionAsync(client, subscription.Id);
        await AdvanceToAsync(host, FirstFailure.AddDays(2));
        var another = await SaveCardAsync(client, customer, "4000000000007726");
        using (var made = await client.PostJsonAsync($"/api/v1/customers/{customer}/default-payment-method", new SetDefaultPaymentMethodRequest { PaymentMethod = another }))
        {
            made.EnsureSuccessStatusCode();
        }

        await host.RunWorkersUntilIdleAsync();

        pastDue.Status.ShouldBe(SubscriptionStatus.PastDue);
        var recovered = await GetSubscriptionAsync(client, subscription.Id);
        (recovered.Status, recovered.PastDueSince).ShouldBe((SubscriptionStatus.Active, null));
        var (first, renewal) = (await NumberAsync(client, subscription.LatestInvoice!.Id), await NumberAsync(client, recovered.LatestInvoice!.Id));
        var received = await mailpit.ReceivedByAsync(email);
        received.Select(message => message.Subject).ShouldBe([$"Ödemeniz alındı: {first}", $"Ödemeniz alınamadı: {renewal}", $"Ödemeniz alındı: {renewal}"]);
    }

    [Fact]
    [Trait("Requirement", "DUN-6")]
    [Trait("Requirement", "NTF-12")]
    public async Task AnUnrecoveredRenewal_IsCanceledOnSchedule()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (customer, email) = await CustomerAsync(client, "en");
        var card = await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await SubscribeAsync(host, client, customer);
        await SetBehaviorAsync(client, card, "insufficient_funds");

        await FailRenewalAsync(host);
        foreach (var day in new[] { 1, 3, 5, 6, 7, 8 })
        {
            await AdvanceToAsync(host, FirstFailure.AddDays(day));
        }

        var canceled = await GetSubscriptionAsync(client, subscription.Id);
        (canceled.Status, canceled.CancellationReason, canceled.EndedAt).ShouldBe((SubscriptionStatus.Canceled, CancellationReason.PaymentFailed, FirstFailure.AddDays(7)));
        var (first, renewal) = (await NumberAsync(client, subscription.LatestInvoice!.Id), await NumberAsync(client, canceled.LatestInvoice!.Id));
        var payments = await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={canceled.LatestInvoice.Id}");
        payments.Items.Count.ShouldBe(5, "the first attempt and the retries of days 1, 3, 5 and 7");
        var received = await mailpit.ReceivedByAsync(email);
        received.Select(message => message.Subject).ShouldBe(
            [$"Payment received: {first}", $"Your payment failed: {renewal}", $"Last reminder: {renewal}", "Your subscription ended"]);
    }

    [Fact]
    [Trait("Requirement", "DUN-7")]
    [Trait("Requirement", "NTF-7")]
    public async Task TheMerchantsFailures_NeverCancelTheCustomer_EvenPastTheDeadline()
    {
        var operatorEmail = $"ops-{Guid.CreateVersion7():N}@example.com";
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Notifications:OperatorEmail"] = operatorEmail });
        using var client = await ClientAsync(host);
        var (customer, email) = await CustomerAsync(client, "tr");
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await SubscribeAsync(host, client, customer);
        await EnableConnectionAsync(client, enabled: false);

        await FailRenewalAsync(host);
        for (var hours = 6; hours <= 10 * 24; hours += 6)
        {
            await AdvanceToAsync(host, FirstFailure.AddHours(hours));
        }

        var waiting = await GetSubscriptionAsync(client, subscription.Id);
        await EnableConnectionAsync(client, enabled: true);
        await AdvanceToAsync(host, FirstFailure.AddDays(11));

        waiting.Status.ShouldBe(SubscriptionStatus.Active);
        var paid = await GetSubscriptionAsync(client, subscription.Id);
        paid.Status.ShouldBe(SubscriptionStatus.Active);
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{paid.LatestInvoice!.Id}")).Status.ShouldBe(InvoiceStatus.Paid);
        var (first, renewal) = (await NumberAsync(client, subscription.LatestInvoice!.Id), await NumberAsync(client, paid.LatestInvoice.Id));
        (await mailpit.ReceivedByAsync(email)).Select(message => message.Subject).ShouldBe([$"Ödemeniz alındı: {first}", $"Ödemeniz alındı: {renewal}"]);
        (await mailpit.ReceivedByAsync(operatorEmail)).Select(message => message.Subject).ShouldBe(["İlgilenilmesi gereken kayıt: merchant_failures"]);
    }

    [Fact]
    [Trait("Requirement", "INV-13")]
    [Trait("Requirement", "INV-14")]
    [Trait("Requirement", "INV-26")]
    public async Task ACompany_PaysItsInvoiceByBankTransfer()
    {
        await using var host = Host(new Dictionary<string, string?>
        {
            ["Tahsil:Invoices:BankTransfer:TRY:Bank"] = "Ziraat Bankası",
            ["Tahsil:Invoices:BankTransfer:TRY:AccountHolder"] = "Örnek Yazılım A.Ş.",
            ["Tahsil:Invoices:BankTransfer:TRY:Iban"] = "TR33 0006 1005 1978 6457 8413 26",
        });
        using var client = await ClientAsync(host);
        var (company, email) = await CustomerAsync(client, "en", CustomerType.Company);
        var subscription = await SendingInvoicesAsync(client, company, daysUntilDue: 14);
        var invoice = subscription.LatestInvoice!.Id;
        await host.RunWorkersUntilIdleAsync();

        await AdvanceToAsync(host, April.AddDays(5));
        await PayAsync(client, invoice, new OutOfBandPaymentRequest { Amount = 10_000, Reference = "EFT 1001" });
        await AdvanceToAsync(host, April.AddDays(9));
        await PayAsync(client, invoice, new OutOfBandPaymentRequest { Reference = "EFT 1002" });
        await AdvanceToAsync(host, April.AddDays(20));

        var paid = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}");
        (paid.Status, paid.AmountPaid).ShouldBe((InvoiceStatus.Paid, 30_000L));
        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Active);
        var received = await mailpit.ReceivedByAsync(email);
        received.Select(message => message.Subject).ShouldBe([$"Your invoice {paid.Number} (₺300.00)", $"Payment received: {paid.Number}"]);
        received[0].Text.ShouldContain("TR33 0006 1005 1978 6457 8413 26");
    }

    /// <summary>A customer of their own, so that what Mailpit received for them is theirs alone.</summary>
    private static async Task<(Guid Id, string Email)> CustomerAsync(HttpClient client, string locale, CustomerType type = CustomerType.Individual)
    {
        var email = $"customer-{Guid.CreateVersion7():N}@example.com";
        using var response = await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Deniz Kaya", Email = email, Type = type, Locale = locale });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return ((await response.ReadAsync<CustomerResponse>()).Id, email);
    }

    /// <summary>A monthly Team subscription, paid on April 1 with the customer's card.</summary>
    private static async Task<SubscriptionResponse> SubscribeAsync(WebApplicationFactory<Program> host, HttpClient client, Guid customer)
    {
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        await host.RunWorkersUntilIdleAsync();
        return subscription;
    }

    private static async Task<string> NumberAsync(HttpClient client, Guid invoice) => (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).Number!;

    private static async Task PayAsync(HttpClient client, Guid invoice, OutOfBandPaymentRequest received)
    {
        using var response = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { OutOfBand = received });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
    }

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
        _time.Advance(instant - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null)
    {
        var all = mailpit.Settings;
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        return IsolatedHosts.Create(server, server.NewDatabase("collection_exit"), _time, settings: all);
    }
}
