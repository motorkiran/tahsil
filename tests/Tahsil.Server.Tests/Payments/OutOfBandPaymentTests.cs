using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;

namespace Tahsil.Server.Tests.Payments;

/// <summary>Money received outside Tahsil, such as bank transfers (INV-14, INV-15, PAY-21).</summary>
public sealed class OutOfBandPaymentTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.CustomersWrite, TahsilPermissions.PaymentsRead,
        TahsilPermissions.RefundsWrite, TahsilPermissions.AuditRead,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "INV-14")]
    [Trait("Requirement", "INV-15")]
    public async Task ABankTransfer_IsRecordedInParts_UntilTheInvoiceIsPaid()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        var receivedAt = _time.GetUtcNow().AddHours(-3);

        var part = await PayAsync(client, invoice, new OutOfBandPaymentRequest { Amount = 5_000, ReceivedAt = receivedAt, Reference = " EFT 4471 ", Note = "Received at Ziraat" });
        var afterPart = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}");
        var rest = await PayAsync(client, invoice, new OutOfBandPaymentRequest { Method = OutOfBandMethod.Cash });
        var paid = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}");

        (part.Status, part.Initiator, part.Method, part.ConnectionId, part.Amount).ShouldBe((PaymentStatus.Succeeded, PaymentInitiator.Operator, PaymentMethodKind.BankTransfer, null, 5_000L));
        part.OutOfBand.ShouldBe(new OutOfBandResponse(receivedAt, "EFT 4471", "Received at Ziraat"));
        (afterPart.Status, afterPart.AmountRemaining).ShouldBe((InvoiceStatus.Open, 7_500L));
        (rest.Method, rest.Amount).ShouldBe((PaymentMethodKind.Cash, 7_500L));
        (paid.Status, paid.AmountPaid, paid.AmountRemaining).ShouldBe((InvoiceStatus.Paid, 12_500L, 0L));
        (await host.QueryAsync<string>($"SELECT actor_type AS \"Value\" FROM tahsil.audit_events WHERE action = 'payment.succeeded'")).ShouldBe(["api_key", "api_key"]);
    }

    [Theory]
    [Trait("Requirement", "INV-14")]
    [InlineData(20_000L, "invoice.overpayment")]
    [InlineData(0L, "invoice.overpayment")]
    public async Task MoreThanIsLeft_IsRefused(long amount, string code)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);

        using var response = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { OutOfBand = new OutOfBandPaymentRequest { Amount = amount } });
        using var both = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { PaymentMethod = Guid.CreateVersion7(), OutOfBand = new OutOfBandPaymentRequest() });

        (await response.ProblemCodeAsync()).ShouldBe(code);
        (await both.ProblemCodeAsync()).ShouldBe("payment.invalid");
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).AmountRemaining.ShouldBe(12_500L);
    }

    [Fact]
    [Trait("Requirement", "PAY-21")]
    public async Task ARefundOfMoneyReceivedOutside_IsRecordedAsDoneAtOnce()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        var payment = await PayAsync(client, invoice, new OutOfBandPaymentRequest());

        using var refunded = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment.Id, Amount = 2_000 });

        refunded.StatusCode.ShouldBe(HttpStatusCode.Created, await refunded.Content.ReadAsStringAsync(CancellationToken));
        var refund = await refunded.ReadAsync<RefundResponse>();
        (refund.Status, refund.Kind, refund.Amount).ShouldBe((RefundStatus.Succeeded, RefundKind.OutOfBand, 2_000L));
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).AmountRefunded.ShouldBe(2_000L);
    }

    private static async Task<PaymentResponse> PayAsync(HttpClient client, Guid invoice, OutOfBandPaymentRequest received)
    {
        using var response = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { OutOfBand = received });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<PaymentResponse>();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("out_of_band"), _time);
}
