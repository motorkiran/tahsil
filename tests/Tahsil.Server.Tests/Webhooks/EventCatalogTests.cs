using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Webhooks;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;

namespace Tahsil.Server.Tests.Webhooks;

/// <summary>
/// Every event type of the catalog, emitted by the end-to-end scenario that should emit it, with at least the
/// <c>details</c> fields of EVT §2 (a table over one billing story: a customer, cards, invoices, payments and refunds). Types no
/// milestone emits yet are listed with the one that brings them, so the table grows with the catalog.
/// </summary>
[Trait("Requirement", "EVT-5")]
public sealed class EventCatalogTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersWrite, TahsilPermissions.CustomersRead, TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite,
        TahsilPermissions.PaymentsRead, TahsilPermissions.RefundsWrite, TahsilPermissions.ConnectionsWrite, TahsilPermissions.WebhooksWrite,
        TahsilPermissions.TestWrite, TahsilPermissions.CheckoutWrite,
    ];

    /// <summary>Catalog types that no M2 scenario emits, and the milestone that emits them.</summary>
    private static readonly Dictionary<string, string> EmittedLater = new(StringComparer.Ordinal)
    {
        [TahsilEventTypes.PaymentMethodWillExpire] = "M5: card expiry reminders for running subscriptions (CUS-15)",
        [TahsilEventTypes.InvoiceCreated] = "M4: cycle invoices with a draft period (INV-5)",
        [TahsilEventTypes.InvoiceActionRequired] = "M5: dunning asks the customer to authenticate (DUN-4)",
        [TahsilEventTypes.InvoiceOverdue] = "M5: overdue invoices (INV-13)",
    };

    private static readonly SemaphoreSlim StoryGate = new(1, 1);
    private static IReadOnlyList<WebhookEvent>? _story;

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);
    private readonly TestWebhookReceiver _receiver = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static TheoryData<string, string[]> Catalog => new()
    {
        { TahsilEventTypes.CustomerCreated, ["externalId"] },
        { TahsilEventTypes.CustomerUpdated, ["changed"] },
        { TahsilEventTypes.CustomerDeleted, ["anonymized"] },
        { TahsilEventTypes.PaymentMethodAttached, ["brand", "last4", "expMonth", "expYear", "connectionId"] },
        { TahsilEventTypes.PaymentMethodRemoved, ["brand", "last4"] },
        { TahsilEventTypes.InvoiceFinalized, ["number", "total", "amountDue", "currency", "dueDate", "subscriptionId"] },
        { TahsilEventTypes.InvoicePaid, ["number", "total", "amountPaid", "currency", "subscriptionId", "billingReason"] },
        { TahsilEventTypes.InvoicePaymentFailed, ["number", "amountDue", "currency", "attempt", "failureCode", "failureCategory", "nextAttemptAt"] },
        { TahsilEventTypes.InvoiceVoided, ["number"] },
        { TahsilEventTypes.InvoiceMarkedUncollectible, ["number", "amountRemaining"] },
        { TahsilEventTypes.PaymentSucceeded, ["invoiceId", "amount", "currency", "method", "connectionId"] },
        { TahsilEventTypes.PaymentFailed, ["invoiceId", "amount", "currency", "failureCode", "failureCategory"] },
        { TahsilEventTypes.RefundSucceeded, ["paymentId", "invoiceId", "amount", "currency", "reason"] },
        { TahsilEventTypes.RefundFailed, ["paymentId", "amount", "currency", "failureCode"] },
        { TahsilEventTypes.CheckoutExpired, ["mode", "clientReferenceId"] },
        { TahsilEventTypes.CheckoutCompleted, ["mode", "clientReferenceId", "subscriptionId", "invoiceId", "paymentMethodId"] },
    };

    [Fact]
    public void TheTable_CoversTheCatalog()
    {
        var covered = Catalog.Select(row => (string)row.Data.Item1).Concat(EmittedLater.Keys);

        covered.ShouldBe(TahsilEventTypes.All, ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Catalog))]
    public async Task EachEvent_IsEmittedByItsScenario_WithItsDetails(string type, string[] details)
    {
        var events = await StoryAsync();

        var emitted = events.Where(candidate => candidate.Type == type).ToList();
        emitted.ShouldNotBeEmpty($"no scenario emitted {type}");
        foreach (var @event in emitted)
        {
            var fields = @event.Data.Details.ShouldNotBeNull().EnumerateObject().Select(property => property.Name).ToList();
            // The audit event's data is the details (EVT-4), so events may carry more than the table; adding is non-breaking (EVT-6).
            details.ShouldBeSubsetOf(fields, $"{type} details");
        }
    }

    /// <summary>The billing story, told once for the table: the events an application's endpoint received.</summary>
    private async Task<IReadOnlyList<WebhookEvent>> StoryAsync()
    {
        await StoryGate.WaitAsync(CancellationToken);
        try
        {
            return _story ??= await TellAsync();
        }
        finally
        {
            StoryGate.Release();
        }
    }

    private async Task<IReadOnlyList<WebhookEvent>> TellAsync()
    {
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("catalog"), _time, services =>
            services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver.CreateHandler()));
        using var client = await host.CreateClientWithAsync(Permissions);
        var url = _receiver.NewEndpoint();
        (await SendAsync(client, HttpMethod.Post, "/api/v1/webhooks/endpoints", new CreateWebhookEndpointRequest { Url = url })).Dispose();
        (await SendAsync(client, HttpMethod.Patch, "/api/v1/provider-connections/simulator", new { settings = new { notifications = false } })).Dispose();

        // A customer whose details change, who saves a card by paying on the page and gets a refund.
        var customer = await CustomerAsync(client);
        (await SendAsync(client, HttpMethod.Patch, $"/api/v1/customers/{customer}", new UpdateCustomerRequest { Name = "Ayşe Demir" })).Dispose();
        var paid = await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice), "4242424242424242");
        (await SendAsync(client, HttpMethod.Post, "/api/v1/refunds", new CreateRefundRequest { Payment = paid.Id })).Dispose();

        // The card declines a renewal; the invoice is given up on, then voided.
        var card = paid.PaymentMethodId!.Value;
        (await SendAsync(client, HttpMethod.Patch, $"/api/v1/test/simulator/payment-methods/{card}", new UpdateSimulatorPaymentMethodRequest { Behavior = "card_declined" })).Dispose();
        var declined = await FinalizedInvoiceAsync(client, customer);
        await host.RunWorkersUntilIdleAsync();
        (await SendAsync(client, HttpMethod.Post, $"/api/v1/invoices/{declined}/mark-uncollectible")).Dispose();
        (await SendAsync(client, HttpMethod.Post, $"/api/v1/invoices/{declined}/void")).Dispose();
        (await SendAsync(client, HttpMethod.Delete, $"/api/v1/payment-methods/{card}")).Dispose();

        // A payment whose refund the provider declines.
        var unrefundable = await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice), "4000000000005126");
        (await SendAsync(client, HttpMethod.Post, "/api/v1/refunds", new CreateRefundRequest { Payment = unrefundable.Id })).Dispose();

        // A checkout the customer never finished.
        using (var checkout = await SendAsync(client, HttpMethod.Post, "/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Setup,
            Currency = "TRY",
            SuccessUrl = "https://app.example.com/billing",
            ClientReferenceId = "order-1",
        }))
        {
            var session = await checkout.ReadAsync<CheckoutSessionResponse>();
            (await SendAsync(client, HttpMethod.Post, $"/api/v1/checkout-sessions/{session.Id}/expire")).Dispose();
        }

        // A purchase on the checkout page.
        using (var purchase = await SendAsync(client, HttpMethod.Post, "/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Setup,
            Currency = "TRY",
            SuccessUrl = "https://app.example.com/billing",
            ClientReferenceId = "order-2",
        }))
        {
            var session = await purchase.ReadAsync<CheckoutSessionResponse>();
            using var browser = Checkout.CheckoutBrowser.Open(host);
            var form = Checkout.CheckoutBrowser.Form("ada@example.com", "Ada", []);
            form["Form.CardStorage"] = "true";
            using var submitted = await Checkout.CheckoutBrowser.SubmitAsync(browser, session.Url!, form);
            (await Checkout.CheckoutBrowser.PayOnTheEmbedAsync(host, browser, submitted, "4242424242424242")).Dispose();
        }

        // A customer without history, deleted.
        (await SendAsync(client, HttpMethod.Delete, $"/api/v1/customers/{await CustomerAsync(client)}")).Dispose();

        await host.RunWorkersUntilIdleAsync();
        return [.. _receiver.ReceivedBy(url).Where(webhook => webhook.Answer == HttpStatusCode.OK).Select(webhook => JsonSerializer.Deserialize<WebhookEvent>(webhook.Body, ApiClients.Json)!)];
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: ApiClients.Json);
        }

        var response = await client.SendAsync(request, CancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {path}: {await response.Content.ReadAsStringAsync(CancellationToken)}");
        return response;
    }
}
