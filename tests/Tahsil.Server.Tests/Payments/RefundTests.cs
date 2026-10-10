using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using PaymentStatus = Tahsil.Contracts.PaymentStatus;
using RefundKind = Tahsil.Contracts.RefundKind;
using RefundStatus = Tahsil.Contracts.RefundStatus;

namespace Tahsil.Server.Tests.Payments;

/// <summary>Refunds through the simulator, on a billing clock the tests move.</summary>
public sealed class RefundTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.CustomersWrite, TahsilPermissions.PaymentsRead,
        TahsilPermissions.RefundsWrite, TahsilPermissions.ConnectionsRead, TahsilPermissions.ConnectionsWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PAY-20")]
    [Trait("Requirement", "PAY-22")]
    public async Task AFullRefundOnTheDayOfThePayment_IsAVoid_AndChangesNoStatus()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var (invoice, payment) = await PaidAsync(host, client, "4242424242424242");

        var refund = await RefundAsync(client, new CreateRefundRequest { Payment = payment, Note = "Customer changed their mind" });
        var paid = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}");
        var charged = await GetAsync<PaymentResponse>(client, $"/api/v1/payments/{payment}");

        (refund.Status, refund.Kind, refund.Amount, refund.Reason).ShouldBe((RefundStatus.Succeeded, RefundKind.Void, 12_500, RefundReason.RequestedByCustomer));
        refund.Reference.ShouldBe(refund.Id.ToString("N"));
        refund.ProviderRefundId.ShouldStartWith("sim_ref_");
        (paid.Status, paid.AmountRefunded).ShouldBe((InvoiceStatus.Paid, 12_500));
        (charged.Status, charged.AmountRefunded).ShouldBe((PaymentStatus.Succeeded, 12_500));
        // The fake clock stands still, so both events have the same instant and no order.
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE subject_id = {refund.Id.ToString()}"))
            .ShouldBe(["refund.created", "refund.succeeded"], ignoreOrder: true);
    }

    [Fact]
    [Trait("Requirement", "PAY-18")]
    [Trait("Requirement", "DOM-19")]
    public async Task PartialRefunds_NeverExceedThePayment()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var (_, payment) = await PaidAsync(host, client, "4242424242424242");
        _time.Advance(TimeSpan.FromDays(1));

        using var tooMuch = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment, Amount = 12_501 });
        var racing = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment, Amount = 3_000 })));
        var rest = await RefundAsync(client, new CreateRefundRequest { Payment = payment });
        using var nothingLeft = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment });

        (await tooMuch.ProblemCodeAsync()).ShouldBe("refund.amount_too_large");
        racing.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(4);
        racing.Count(response => response.StatusCode == HttpStatusCode.BadRequest).ShouldBe(2);
        (rest.Amount, rest.Kind).ShouldBe((500, RefundKind.Refund));
        (await nothingLeft.ProblemCodeAsync()).ShouldBe("refund.payment_not_refundable");
        (await GetAsync<PaymentResponse>(client, $"/api/v1/payments/{payment}")).AmountRefunded.ShouldBe(12_500);
        foreach (var response in racing)
        {
            response.Dispose();
        }
    }

    [Fact]
    [Trait("Requirement", "PAY-19")]
    public async Task AsynchronousRefunds_SettleThroughReconciliation()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var (invoice, payment) = await PaidAsync(host, client, "4000000000007726");

        var refund = await RefundAsync(client, new CreateRefundRequest { Payment = payment, Amount = 2_500 });
        for (var minute = 0; minute < 90; minute += 5)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await host.RunWorkersUntilIdleAsync();
        }

        var settled = await GetAsync<RefundResponse>(client, $"/api/v1/refunds/{refund.Id}");
        refund.Status.ShouldBe(RefundStatus.Processing);
        settled.Status.ShouldBe(RefundStatus.Succeeded);
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).AmountRefunded.ShouldBe(2_500);
    }

    [Fact]
    [Trait("Requirement", "PAY-19")]
    public async Task AFailedRefund_IsReported_AndLeavesThePaymentRefundable()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var (_, payment) = await PaidAsync(host, client, "4000000000005126");

        var refund = await RefundAsync(client, new CreateRefundRequest { Payment = payment });
        var listed = await GetAsync<Page<RefundResponse>>(client, $"/api/v1/refunds?payment={payment}&status=failed");

        (refund.Status, refund.FailureCode).ShouldBe((RefundStatus.Failed, "refund_declined"));
        listed.Items.ShouldHaveSingleItem().Id.ShouldBe(refund.Id);
        (await GetAsync<PaymentResponse>(client, $"/api/v1/payments/{payment}")).AmountRefunded.ShouldBe(0);
        (await host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items WHERE subject_id = {refund.Id}")).ShouldBe(["refund_failed"]);
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE action = 'refund.failed'")).ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Requirement", "PAY-18")]
    public async Task OnlySucceededPayments_CanBeRefunded()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var declined = await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, customer), "4000000000000002");

        using var response = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = declined.Id });
        using var unknown = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = Guid.CreateVersion7() });

        (await response.ProblemCodeAsync()).ShouldBe("refund.payment_not_refundable");
        (await unknown.ProblemCodeAsync()).ShouldBe("payment.not_found");
    }

    [Fact]
    [Trait("Requirement", "PAY-20")]
    public async Task ProvidersThatRefundPartsOnlyFromTheNextDay_AreNotAskedEarlier()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        using var settings = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { refunds = "partial_after_settlement_day" } }, options: ApiClients.Json),
        }, CancellationToken);
        settings.StatusCode.ShouldBe(HttpStatusCode.OK, await settings.Content.ReadAsStringAsync(CancellationToken));
        var (_, payment) = await PaidAsync(host, client, "4242424242424242");

        using var early = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment, Amount = 1_000 });
        _time.Advance(TimeSpan.FromDays(1));
        var nextDay = await RefundAsync(client, new CreateRefundRequest { Payment = payment, Amount = 1_000 });

        (await early.ProblemCodeAsync()).ShouldBe("refund.not_allowed_yet");
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.simulator_transactions WHERE kind = 'refund'")).ShouldBe([1]);
        nextDay.Status.ShouldBe(RefundStatus.Succeeded);
    }

    [Fact]
    [Trait("Requirement", "SEC-13")]
    public async Task EachKeyRefundsUpToItsDailyLimit()
    {
        await using var host = Host(settings: new() { ["Tahsil:Refunds:DailyLimitPerActor:TRY"] = "5000" });
        using var client = await host.CreateClientWithAsync(Permissions);
        var (_, payment) = await PaidAsync(host, client, "4242424242424242");
        _time.Advance(TimeSpan.FromDays(1));

        await RefundAsync(client, new CreateRefundRequest { Payment = payment, Amount = 3_000 });
        using var over = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment, Amount = 3_000 });
        using var other = await host.CreateClientWithAsync(Permissions);
        var otherKey = await RefundAsync(other, new CreateRefundRequest { Payment = payment, Amount = 3_000 });
        _time.Advance(TimeSpan.FromDays(1));
        var tomorrow = await RefundAsync(client, new CreateRefundRequest { Payment = payment, Amount = 3_000 });

        over.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await over.ProblemCodeAsync()).ShouldBe("refund.limit_exceeded");
        (otherKey.Status, tomorrow.Status).ShouldBe((RefundStatus.Succeeded, RefundStatus.Succeeded));
    }

    [Fact]
    [Trait("Requirement", "PAY-14")]
    public async Task ALateSuccessNothingNeeds_IsRefundedAutomatically()
    {
        var provider = new ScriptedProvider();
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        await RouteToScriptedAsync(client);
        var customer = await CustomerAsync(client);
        var invoice = await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice);
        var abandoned = await StartOnPageAsync(host, invoice);
        await ApplyAsync(host, abandoned, PaymentOutcome.Failure(FailureCode.Abandoned));
        var paying = await StartOnPageAsync(host, invoice);
        await ApplyAsync(host, paying, new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded });

        await ApplyAsync(host, abandoned, new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded, ProviderPaymentId = "late" });
        await host.RunWorkersUntilIdleAsync();
        await host.RunWorkersUntilIdleAsync();
        var refunds = await GetAsync<Page<RefundResponse>>(client, $"/api/v1/refunds?payment={abandoned}");

        var refund = refunds.Items.ShouldHaveSingleItem();
        (refund.Status, refund.Reason, refund.Amount, refund.Actor.Type).ShouldBe((RefundStatus.Succeeded, RefundReason.LateSuccess, 12_500, ActorKind.System));
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).AmountPaid.ShouldBe(12_500);
    }

    private WebApplicationFactory<Program> Host(ScriptedProvider? provider = null, Dictionary<string, string?>? settings = null)
    {
        var database = server.NewDatabase("refunds");
        if (settings is null)
        {
            return IsolatedHosts.Create(server, database, _time, Services);
        }

        var all = new Dictionary<string, string?>(settings) { ["ConnectionStrings:Tahsil"] = database, ["Tahsil:Workers:Enabled"] = "false" };
        return server.WithSettings(all, collection =>
        {
            collection.AddSingleton<Application.Common.BillingClock>(new FakeBillingClock(_time));
            Services(collection);
        });

        void Services(IServiceCollection collection)
        {
            if (provider is not null)
            {
                collection.AddSingleton<IPaymentProvider>(provider);
            }
        }
    }

    private static async Task<(Guid Invoice, Guid Payment)> PaidAsync(WebApplicationFactory<Program> host, HttpClient client, string card)
    {
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        var payment = await PayOnPageAsync(host, invoice, card);
        payment.Status.ShouldBe(Domain.Payments.PaymentStatus.Succeeded);
        return (invoice, payment.Id);
    }

    private static async Task RouteToScriptedAsync(HttpClient client)
    {
        using var created = await client.PostJsonAsync("/api/v1/provider-connections", new CreateConnectionRequest
        {
            Key = "scripted-main",
            Provider = ScriptedProvider.Key,
            Name = "Scripted",
            Environment = ConnectionEnvironment.Sandbox,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var routed = await client.PutAsJsonAsync(
            new Uri("/api/v1/provider-connections/routing", UriKind.Relative), new Dictionary<string, string> { ["TRY"] = "scripted-main" }, ApiClients.Json, CancellationToken);
        routed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<RefundResponse> RefundAsync(HttpClient client, CreateRefundRequest request)
    {
        using var response = await client.PostJsonAsync("/api/v1/refunds", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<RefundResponse>();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }
}
