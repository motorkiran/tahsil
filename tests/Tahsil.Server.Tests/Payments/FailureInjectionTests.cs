using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Webhooks;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;
using Tahsil.Server.Tests.Webhooks;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using PaymentStatus = Tahsil.Contracts.PaymentStatus;
using RefundStatus = Tahsil.Contracts.RefundStatus;

namespace Tahsil.Server.Tests.Payments;

/// <summary>
/// The M2 exit suite (PAY §13): payments through the real simulator, with its failure cards and injected process
/// failures, from the API to the webhooks applications receive. Workers run when the tests say, on a clock they move.
/// </summary>
[Trait("Suite", "FailureInjection")]
public sealed partial class FailureInjectionTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersWrite, TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.PaymentsRead,
        TahsilPermissions.RefundsWrite, TahsilPermissions.ConnectionsWrite, TahsilPermissions.WebhooksWrite, TahsilPermissions.TestWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);
    private readonly TestWebhookReceiver _receiver = new();
    private readonly Faults _faults = new();
    private string _webhooks = string.Empty;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "ENG-15")]
    [Trait("Requirement", "ADP-4")]
    public async Task ATimeoutBeforeTheProviderGotTheCharge_FailsAsNotSubmitted_AndTheRetryChargesOnce()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        var card = await SavedCardAsync(host, client, customer, "4000000000001018");
        var invoice = await FinalizedInvoiceAsync(client, customer);
        await host.RunWorkersUntilIdleAsync();
        (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Processing);

        await AdvanceAsync(host, TimeSpan.FromMinutes(1), times: 25);
        var failed = (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem();
        await ChangeCardAsync(client, card, "succeed");
        var retried = await PayAsync(client, invoice, card);
        await host.RunWorkersUntilIdleAsync();

        (failed.Status, failed.FailureCode).ShouldBe((PaymentStatus.Failed, "not_submitted"));
        retried.Status.ShouldBe(PaymentStatus.Succeeded);
        (await InvoiceAsync(client, invoice)).AmountPaid.ShouldBe(12_500);
        (await SimulatorChargesAsync(host)).ShouldBe(2, "the card's first payment and the retry; the timed-out charge never reached the provider");
        Events().Count(type => type == TahsilEventTypes.PaymentFailed).ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "ENG-15")]
    public async Task ATimeoutAfterTheProviderCharged_IsSettledByReconciliation_AndNeverChargedAgain()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SavedCardAsync(host, client, customer, "4000000000001000");
        var invoice = await FinalizedInvoiceAsync(client, customer);
        await host.RunWorkersUntilIdleAsync();

        await AdvanceAsync(host, TimeSpan.FromMinutes(1), times: 2);
        var settled = (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem();
        await AdvanceAsync(host, TimeSpan.FromHours(1), times: 48);

        settled.Status.ShouldBe(PaymentStatus.Succeeded);
        (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem("no next attempt");
        var paid = await InvoiceAsync(client, invoice);
        (paid.Status, paid.AttemptCount, paid.NextPaymentAttemptAt).ShouldBe((InvoiceStatus.Paid, 1, null));
        (await SimulatorChargesAsync(host)).ShouldBe(2);
    }

    /// <remarks>
    /// The killed attempt's work item is retried after the restart, once its payment is settled: a charge that never
    /// reached the provider is made again, one that did leaves the invoice paid. Never twice.
    /// </remarks>
    [Theory]
    [Trait("Requirement", "ENG-13")]
    [Trait("Requirement", "ENG-15")]
    [Trait("Requirement", "ENG-6")]
    [InlineData(ProcessDeath.BeforeTheCharge, new[] { "failed:not_submitted", "succeeded" })]
    [InlineData(ProcessDeath.AfterTheCharge, new[] { "succeeded" })]
    public async Task AProcessKilledBetweenWriteAheadAndRecording_IsSettledAfterTheRestart_WithoutADoubleCharge(ProcessDeath death, string[] payments)
    {
        var database = server.NewDatabase("failures");
        Guid invoice;
        await using (var dying = Host(database))
        {
            using var client = await ClientAsync(dying);
            var customer = await CustomerAsync(client);
            await SavedCardAsync(dying, client, customer, "4242424242424242");
            invoice = await FinalizedInvoiceAsync(client, customer);
            _faults.Death = death;
            await dying.RunWorkersUntilIdleAsync();
            (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Processing, "written ahead, never recorded");
        }

        _faults.Death = ProcessDeath.None;
        await using var restarted = Host(database);
        using var again = await restarted.CreateClientWithAsync(Permissions);
        await AdvanceAsync(restarted, TimeSpan.FromMinutes(1), times: 30);

        (await PaymentsAsync(again, invoice)).Select(payment => payment.FailureCode is { } code ? $"failed:{code}" : payment.Status.ToString().ToLowerInvariant())
            .ShouldBe(payments, ignoreOrder: true);
        var paid = await InvoiceAsync(again, invoice);
        (paid.Status, paid.AmountPaid).ShouldBe((InvoiceStatus.Paid, 12_500));
        (await SimulatorChargesAsync(restarted)).ShouldBe(2, "the card's first payment and this invoice's, once");
    }

    [Fact]
    [Trait("Requirement", "PAY-13")]
    [Trait("Requirement", "PAY-25")]
    public async Task RepeatedAndOutOfOrderNotifications_ApplyOneOutcome()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        var (payment, token) = await StartAsync(host, invoice);
        await host.Services.GetRequiredService<SimulatorBank>().SubmitCardAsync(token, Card("4242424242424242"), CancellationToken);
        var reference = payment.ToString("N");

        // The later event arrives first, the earlier one after it, and the provider sends the later one twice.
        (await NotifyAsync(host, "evt_2", "payment.succeeded", reference)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NotifyAsync(host, "evt_1", "payment.pending", reference)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NotifyAsync(host, "evt_2", "payment.succeeded", reference)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.RunWorkersUntilIdleAsync();
        await CompleteAsync(host, payment);
        await host.RunWorkersUntilIdleAsync();

        (await PaymentAsync(client, payment)).Status.ShouldBe(PaymentStatus.Succeeded);
        (await InvoiceAsync(client, invoice)).AmountPaid.ShouldBe(12_500);
        (await host.QueryAsync<string>($"SELECT status AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe(["processed", "processed"]);
        Events().Count(type => type == TahsilEventTypes.PaymentSucceeded).ShouldBe(1);
        Events().Count(type => type == TahsilEventTypes.InvoicePaid).ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "PAY-13")]
    public async Task TheReturnAndANotificationArrivingTogether_ApplyOneOutcome()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        var (payment, token) = await StartAsync(host, invoice);
        await host.Services.GetRequiredService<SimulatorBank>().SubmitCardAsync(token, Card("4242424242424242"), CancellationToken);
        (await NotifyAsync(host, "evt_1", "payment.succeeded", payment.ToString("N"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Task.WhenAll(CompleteAsync(host, payment), host.RunWorkersUntilIdleAsync());
        await host.RunWorkersUntilIdleAsync();

        (await PaymentAsync(client, payment)).Status.ShouldBe(PaymentStatus.Succeeded);
        (await InvoiceAsync(client, invoice)).AmountPaid.ShouldBe(12_500);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.audit_events WHERE action = 'payment.succeeded'")).ShouldBe([1]);
        Events().Count(type => type == TahsilEventTypes.PaymentSucceeded).ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "PAY-14")]
    [Trait("Requirement", "ENG-18")]
    public async Task ALateSuccessAfterTheAttemptWasCanceled_IsRefundedAutomatically()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        var (abandoned, token) = await StartAsync(host, invoice);
        await AdvanceAsync(host, TimeSpan.FromMinutes(5), times: 7);
        (await PaymentAsync(client, abandoned)).Status.ShouldBe(PaymentStatus.Canceled);
        await PayOnPageAsync(host, invoice, "4242424242424242");

        // The provider's page was still open: the customer pays there after Tahsil gave up on the attempt and the
        // invoice was paid another way, and the provider says so.
        await host.Services.GetRequiredService<SimulatorBank>().SubmitCardAsync(token, Card("4242424242424242"), CancellationToken);
        (await NotifyAsync(host, "evt_late", "payment.succeeded", abandoned.ToString("N"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.RunWorkersUntilIdleAsync();

        var late = await PaymentAsync(client, abandoned);
        (late.Status, late.AmountRefunded).ShouldBe((PaymentStatus.Succeeded, 12_500));
        var refund = (await GetAsync<Page<RefundResponse>>(client, $"/api/v1/refunds?payment={abandoned}")).Items.ShouldHaveSingleItem();
        (refund.Status, refund.Reason, refund.Actor.Type).ShouldBe((RefundStatus.Succeeded, RefundReason.LateSuccess, ActorKind.System));
        (await InvoiceAsync(client, invoice)).AmountPaid.ShouldBe(12_500);
        Events().ShouldContain(TahsilEventTypes.RefundSucceeded);
    }

    [Fact]
    [Trait("Requirement", "PAY-15")]
    public async Task AChargeOfAnotherAmount_NeedsAnOperator_AndLeavesTheInvoiceUnpaid()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        _faults.ChargedAmountOffset = -100;

        var payment = await PayOnPageAsync(host, invoice, "4242424242424242");
        await host.RunWorkersUntilIdleAsync();

        (await host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items WHERE subject_id = {payment.Id}")).ShouldBe(["amount_mismatch"]);
        var untouched = await InvoiceAsync(client, invoice);
        (untouched.Status, untouched.AmountPaid).ShouldBe((InvoiceStatus.Open, 0));
        Events().ShouldNotContain(TahsilEventTypes.InvoicePaid);
    }

    [Fact]
    [Trait("Requirement", "PAY-19")]
    public async Task AnAsynchronousRefund_SettlesThroughReconciliation_AndAFailedRefundIsAnEvent()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var slow = await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice), "4000000000007726");
        var declining = await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice), "4000000000005126");

        var pending = await RefundAsync(client, slow.Id);
        var failed = await RefundAsync(client, declining.Id);
        await AdvanceAsync(host, TimeSpan.FromMinutes(5), times: 18);

        pending.Status.ShouldBe(RefundStatus.Processing);
        (await GetAsync<RefundResponse>(client, $"/api/v1/refunds/{pending.Id}")).Status.ShouldBe(RefundStatus.Succeeded);
        (failed.Status, failed.FailureCode).ShouldBe((RefundStatus.Failed, "refund_declined"));
        Events().Count(type => type == TahsilEventTypes.RefundSucceeded).ShouldBe(1);
        Events().Count(type => type == TahsilEventTypes.RefundFailed).ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "PAY-23")]
    [Trait("Requirement", "SEC-4")]
    public async Task ANotificationWithABadSignature_IsRejected_AndNothingIsStored()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        var (payment, _) = await StartAsync(host, invoice);

        using var forged = await NotifyAsync(host, "evt_1", "payment.succeeded", payment.ToString("N"), signature: "t=1,v1=" + new string('0', 64));
        await host.RunWorkersUntilIdleAsync();

        forged.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe([0]);
        (await PaymentAsync(client, payment)).Status.ShouldBe(PaymentStatus.RequiresAction);
        (await InvoiceAsync(client, invoice)).Status.ShouldBe(InvoiceStatus.Open);
    }

    private WebApplicationFactory<Program> Host(string? database = null) =>
        IsolatedHosts.Create(server, database ?? server.NewDatabase("failures"), _time, services =>
        {
            services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver.CreateHandler());
            FaultySimulator.Inject(services, _faults);
        });

    /// <summary>
    /// An API client for a fresh installation whose simulator does not notify itself (the tests send notifications
    /// when they mean to) and whose events go to a webhook endpoint the tests read.
    /// </summary>
    private async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host)
    {
        var client = await host.CreateClientWithAsync(Permissions);
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { notifications = false } }, options: ApiClients.Json),
        };
        using var patched = await client.SendAsync(request, CancellationToken);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync(CancellationToken));
        _webhooks = _receiver.NewEndpoint();
        using var endpoint = await client.PostJsonAsync("/api/v1/webhooks/endpoints", new CreateWebhookEndpointRequest { Url = _webhooks });
        endpoint.StatusCode.ShouldBe(HttpStatusCode.Created, await endpoint.Content.ReadAsStringAsync(CancellationToken));
        return client;
    }

    /// <summary>The types of the events the application received so far.</summary>
    private List<string> Events() =>
        [.. _receiver.ReceivedBy(_webhooks).Where(webhook => webhook.Answer == HttpStatusCode.OK).Select(webhook => JsonDocument.Parse(webhook.Body).RootElement.GetProperty("type").GetString()!)];

    private async Task AdvanceAsync(WebApplicationFactory<Program> host, TimeSpan step, int times)
    {
        for (var time = 0; time < times; time++)
        {
            _time.Advance(step);
            await host.RunWorkersUntilIdleAsync();
        }
    }

    /// <summary>Saves a simulator card as the customer's default, by paying a first invoice with it on the card page.</summary>
    private static async Task<Guid> SavedCardAsync(WebApplicationFactory<Program> host, HttpClient client, Guid customer, string card) =>
        (await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice), card)).PaymentMethodId!.Value;

    private static async Task ChangeCardAsync(HttpClient client, Guid card, string behavior)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/test/simulator/payment-methods/{card}", UriKind.Relative))
        {
            Content = JsonContent.Create(new UpdateSimulatorPaymentMethodRequest { Behavior = behavior }, options: ApiClients.Json),
        };
        using var response = await client.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>Starts a customer-present payment and returns the simulator page's token, as the customer's browser has it.</summary>
    private static async Task<(Guid Payment, string Token)> StartAsync(WebApplicationFactory<Program> host, Guid invoice)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var started = (await scope.ServiceProvider.GetRequiredService<CustomerPayments>().StartAsync(Start(invoice), CancellationToken)).Value;
        return (started.Payment.Id, TokenPattern().Match(started.Presentation.ShouldBeOfType<Presentation.Embed>().Html).Groups["token"].Value);
    }

    /// <summary>The customer comes back to Tahsil from the provider's page.</summary>
    private static async Task CompleteAsync(WebApplicationFactory<Program> host, Guid payment)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CustomerPayments>().CompleteAsync(payment, new Dictionary<string, string>(), CancellationToken);
    }

    private static CardEntryForm Card(string number) => new(number, "12/30", "123", SaveCard: false);

    /// <summary>Posts a simulator notification, signed with the connection's key unless <paramref name="signature"/> is given.</summary>
    private async Task<HttpResponseMessage> NotifyAsync(WebApplicationFactory<Program> host, string id, string type, string reference, string? signature = null)
    {
        var connection = (await host.QueryAsync<Guid>($"SELECT id AS \"Value\" FROM tahsil.provider_connections WHERE key = 'simulator'")).Single();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, type, reference }));
        signature ??= await host.Services.GetRequiredService<SimulatorBank>().SignAsync(connection, _time.GetUtcNow().ToUnixTimeSeconds(), body, CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/provider-notifications/{connection}", UriKind.Relative)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(SimulatorBank.SignatureHeader, signature);
        using var client = host.CreateClient();
        return await client.SendAsync(request, CancellationToken);
    }

    /// <summary>Charges the simulator made: payments it holds as succeeded.</summary>
    private static async Task<int> SimulatorChargesAsync(WebApplicationFactory<Program> host) =>
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.simulator_transactions WHERE kind = 'payment' AND state->>'status' = 'succeeded'")).Single();

    private static async Task<PaymentResponse> PayAsync(HttpClient client, Guid invoice, Guid card)
    {
        using var response = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { PaymentMethod = card });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<PaymentResponse>();
    }

    private static async Task<RefundResponse> RefundAsync(HttpClient client, Guid payment)
    {
        using var response = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<RefundResponse>();
    }

    private static async Task<IReadOnlyList<PaymentResponse>> PaymentsAsync(HttpClient client, Guid invoice) =>
        (await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={invoice}")).Items;

    private static Task<PaymentResponse> PaymentAsync(HttpClient client, Guid payment) => GetAsync<PaymentResponse>(client, $"/api/v1/payments/{payment}");

    private static Task<InvoiceResponse> InvoiceAsync(HttpClient client, Guid invoice) => GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}");

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    [GeneratedRegex("simulator/pay/(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();
}
