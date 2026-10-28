using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Notifications;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Webhooks;
using Tahsil.Server.Tests.Notifications;
using Tahsil.Server.Tests.Webhooks;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>
/// The M3 exit scenario with the simulator: an application sells a one-time product and saves a card through the
/// hosted checkout, and everything that should follow does: the paid invoice and its page, the saved card, the evidence,
/// the confirmation emails and the webhooks.
/// </summary>
[Trait("Suite", "Exit")]
public sealed class CheckoutExitTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CheckoutWrite, TahsilPermissions.SubscriptionsRead, TahsilPermissions.ProvisioningWrite, TahsilPermissions.CatalogWrite,
        TahsilPermissions.ConnectionsWrite, TahsilPermissions.WebhooksWrite, TahsilPermissions.InvoicesRead, TahsilPermissions.CustomersRead,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);
    private readonly CapturingEmailSender _email = new();
    private readonly TestWebhookReceiver _receiver = new();

    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CHK-23")]
    [Trait("Requirement", "NTF-3")]
    [Trait("Requirement", "INV-24")]
    public async Task ACustomer_BuysAOneTimeProduct_ThenSavesACard_ThroughTheHostedCheckout()
    {
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("checkout_exit"), _time, services =>
        {
            services.AddSingleton<IEmailSender>(_email);
            services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver.CreateHandler());
        });
        using var app = await host.CreateClientWithAsync(Permissions);
        var webhooks = await SetUpAsync(app);

        // The purchase: the application creates a session for a known user; the customer pays on the page.
        var purchase = await CreateAsync(app, new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Payment,
            Items = [new CheckoutItemRequest { Price = "credits-1000", Quantity = 2 }],
            CustomerDetails = new CheckoutCustomerDetails { ExternalId = "user-42", Email = "ayse@example.com", Locale = "tr" },
            SuccessUrl = "https://app.example.com/billing?session={CHECKOUT_SESSION_ID}",
            ClientReferenceId = "order-881",
        });
        using var browser = Open(host);
        using (var submitted = await SubmitAsync(browser, purchase.Url!, Form("ayse@example.com", "Ayşe Yılmaz", ["pre-information", "distance-sales"], immediateStart: true)))
        using (var returned = await PayOnTheEmbedAsync(host, browser, submitted, "4242424242424242"))
        {
            (await ParseAsync(returned)).QuerySelector("#continue")!.GetAttribute("href").ShouldBe($"https://app.example.com/billing?session={purchase.Id}");
        }

        // Then a card for later, saved without a payment.
        var setup = await CreateAsync(app, new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Setup,
            Currency = "TRY",
            CustomerDetails = new CheckoutCustomerDetails { ExternalId = "user-42", Locale = "tr" },
            SuccessUrl = "https://app.example.com/billing",
        });
        var form = Form("ayse@example.com", "Ayşe Yılmaz", []);
        form["Form.CardStorage"] = "true";
        using (var submitted = await SubmitAsync(browser, setup.Url!, form))
        {
            (await PayOnTheEmbedAsync(host, browser, submitted, "4242424242424242")).Dispose();
        }

        await host.RunWorkersUntilIdleAsync();

        // The application sees both sessions complete, for one customer.
        var paid = await GetAsync<CheckoutSessionResponse>(app, $"/api/v1/checkout-sessions/{purchase.Id}");
        var saved = await GetAsync<CheckoutSessionResponse>(app, $"/api/v1/checkout-sessions/{setup.Id}");
        (paid.Status, saved.Status).ShouldBe((CheckoutStatus.Complete, CheckoutStatus.Complete));
        saved.CustomerId.ShouldBe(paid.CustomerId, "the external ID finds the customer the purchase created");
        saved.PaymentMethodId.ShouldNotBeNull();

        // The invoice is paid, numbered, and has its page.
        var invoice = await GetAsync<InvoiceResponse>(app, $"/api/v1/invoices/{paid.InvoiceId}");
        (invoice.Status, invoice.BillingReason, invoice.Total, invoice.AmountPaid).ShouldBe((InvoiceStatus.Paid, BillingReason.Checkout, 59_800L, 59_800L));
        using (var page = await browser.GetAsync(invoice.HostedInvoiceUrl, CancellationToken))
        {
            (await ParseAsync(page)).QuerySelector("h1")!.TextContent.ShouldBe("Tahsilat Makbuzu");
        }

        // The evidence: the accepted documents and consents of both sessions.
        var consents = await QueryAsync(host, context => context.ConsentRecords.Where(record => record.CustomerId == paid.CustomerId)
            .Select(record => record.Type.ToString()).ToListAsync(CancellationToken));
        consents.ShouldBe(["PreInformation", "DistanceSalesContract", "ImmediateStart", "CardStorage"], ignoreOrder: true);

        // The same evidence through the API, with the documents as the customer saw them when asked for, and in the customer's export (SEC-15).
        var listed = (await GetAsync<Page<ConsentRecordResponse>>(app, $"/api/v1/customers/{paid.CustomerId}/consents")).Items;
        listed.Select(consent => consent.Type).ShouldBe([ConsentType.PreInformation, ConsentType.DistanceSalesContract, ConsentType.ImmediateStart, ConsentType.CardStorage], ignoreOrder: true);
        listed.ShouldAllBe(consent => consent.Document == null && consent.TextShown.Length > 0);
        var contract = listed.Single(consent => consent.Type == ConsentType.DistanceSalesContract);
        (contract.DocumentKey, contract.DocumentVersion, contract.CheckoutSessionId).ShouldBe(("distance-sales", "2026-10", purchase.Id));
        var shown = (await GetAsync<Page<ConsentRecordResponse>>(app, $"/api/v1/customers/{paid.CustomerId}/consents?includeDocuments=true")).Items
            .Single(consent => consent.Type == ConsentType.DistanceSalesContract).Document!;
        shown.ShouldContain("Ayşe Yılmaz");
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(shown))).ShouldBe(contract.DocumentHash);
        var export = await GetAsync<CustomerExportResponse>(app, $"/api/v1/customers/{paid.CustomerId}/export");
        (export.Invoices.Count, export.Refunds.Count, export.PaymentMethods.Count, export.CheckoutSessions.Count, export.Consents.Count, export.Subscriptions.Count)
            .ShouldBe((1, 0, 1, 2, 4, 0));
        export.Payments.Select(payment => payment.Amount).ShouldBe([59_800L, 0L], ignoreOrder: true, "the purchase, and the card saved without a purchase");
        export.Invoices[0].Number.ShouldBe(invoice.Number);
        export.Notifications.Select(notification => notification.Sent).ShouldBe([true, true]);
        using (var unknown = await app.GetPathAsync($"/api/v1/customers/{Guid.CreateVersion7()}/consents"))
        {
            (unknown.StatusCode, await unknown.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "customer.not_found"));
        }

        await using (var admin = new Admin.AdminComponents(host))
        {
            var customerPage = admin.Render<Tahsil.Admin.Components.Pages.Customers.CustomerDetail>(parameters => parameters.Add(page => page.Id, paid.CustomerId!.Value));
            customerPage.WaitForAssertion(() => customerPage.FindAll("#consents tbody tr").Count.ShouldBe(4));
            customerPage.Find("#consents").TextContent.ShouldContain("distance-sales 2026-10");
        }

        // Two confirmations, the purchase's with the documents attached.
        _email.Sent.Select(message => message.Subject).ShouldBe([$"Siparişiniz alındı ({invoice.Number})", "Kartınız kaydedildi"], ignoreOrder: true);
        _email.Sent.Single(message => message.Attachments.Count > 0).Attachments.Select(attachment => attachment.FileName)
            .ShouldBe(["distance-sales-2026-10.html", "pre-information-2026-10.html"]);

        // And the application heard about all of it.
        var events = _receiver.ReceivedBy(webhooks).Select(webhook => JsonDocument.Parse(webhook.Body).RootElement.GetProperty("type").GetString()).ToList();
        events.ShouldContain(TahsilEventTypes.CustomerCreated);
        events.Count(type => type == TahsilEventTypes.CheckoutCompleted).ShouldBe(2);
        events.ShouldContain(TahsilEventTypes.InvoicePaid);
        events.ShouldContain(TahsilEventTypes.PaymentSucceeded);
        events.ShouldContain(TahsilEventTypes.PaymentMethodAttached);
    }

    /// <summary>The catalog from a provisioning document, a webhook endpoint, and a simulator that notifies only when told.</summary>
    private async Task<string> SetUpAsync(HttpClient app)
    {
        using (var provisioned = await app.PostJsonAsync("/api/v1/provisioning", new ProvisioningDocument
        {
            TaxRates = [new ProvisionedTaxRate { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true }],
            Products =
            [
                new ProvisionedProduct
                {
                    Key = "credits",
                    Name = "1,000 credits",
                    Prices = [new ProvisionedPrice { Key = "credits-1000", Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 29_900, TaxRate = "kdv-20" }],
                },
            ],
        }))
        {
            provisioned.StatusCode.ShouldBe(HttpStatusCode.OK, await provisioned.Content.ReadAsStringAsync(CancellationToken));
        }

        using (var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { notifications = false } }, options: ApiClients.Json),
        })
        {
            (await app.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode().Dispose();
        }

        var url = _receiver.NewEndpoint();
        (await app.PostJsonAsync("/api/v1/webhooks/endpoints", new CreateWebhookEndpointRequest { Url = url })).EnsureSuccessStatusCode().Dispose();
        return url;
    }

    private static async Task<CheckoutSessionResponse> CreateAsync(HttpClient app, CreateCheckoutSessionRequest request)
    {
        using var response = await app.PostJsonAsync("/api/v1/checkout-sessions", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<CheckoutSessionResponse>();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }
}
