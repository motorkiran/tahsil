using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using PaymentStatus = Tahsil.Contracts.PaymentStatus;

namespace Tahsil.Server.Tests.Payments;

/// <summary>The payment worker and reconciliation, driven on a billing clock the tests move.</summary>
public sealed class PaymentWorkerTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.CustomersWrite, TahsilPermissions.PaymentsRead,
        TahsilPermissions.ConnectionsRead, TahsilPermissions.ConnectionsWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "ENG-13")]
    [Trait("Requirement", "ENG-11")]
    [InlineData("4242424242424242", PaymentStatus.Succeeded, InvoiceStatus.Paid)]
    [InlineData("4000000000000341", PaymentStatus.Failed, InvoiceStatus.Open)]
    public async Task TheWorker_ChargesTheCustomersDefaultCard_WhenTheAttemptIsDue(string card, PaymentStatus status, InvoiceStatus invoiceStatus)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await WithoutSimulatorNotificationsAsync(client);
        var customer = await CustomerAsync(client);
        await SaveSimulatorCardAsync(host, client, customer, card);

        var invoice = await FinalizedInvoiceAsync(client, customer);
        await host.RunWorkersUntilIdleAsync();
        var payments = await PaymentsAsync(client, invoice);
        var after = await InvoiceAsync(client, invoice);

        payments.ShouldHaveSingleItem().Status.ShouldBe(status);
        payments[0].Initiator.ShouldBe(PaymentInitiator.Merchant);
        (after.Status, after.AttemptCount, after.NextPaymentAttemptAt).ShouldBe((invoiceStatus, 1, null));
    }

    [Fact]
    [Trait("Requirement", "ENG-13")]
    public async Task WithoutAUsableCard_TheAttemptIsRecordedAsFailed_WithoutAProviderCall()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);

        var invoice = await FinalizedInvoiceAsync(client, customer);
        await host.RunWorkersUntilIdleAsync();
        var payment = (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem();

        (payment.Status, payment.FailureCode, payment.FailureCategory, payment.ConnectionId).ShouldBe((PaymentStatus.Failed, "payment_method_missing", "new_payment_method", null));
        (await InvoiceAsync(client, invoice)).AttemptCount.ShouldBe(1);
        (await host.QueryAsync<string>($"SELECT data->>'failureCode' AS \"Value\" FROM tahsil.audit_events WHERE action = 'invoice.payment_failed'")).ShouldBe(["payment_method_missing"]);
    }

    [Theory]
    [Trait("Requirement", "ENG-15")]
    [Trait("Requirement", "ADP-4")]
    [InlineData("4000000000001000", 2, PaymentStatus.Succeeded, null)]
    [InlineData("4000000000001018", 2, PaymentStatus.Processing, null)]
    [InlineData("4000000000001018", 21, PaymentStatus.Processing, null)]
    [InlineData("4000000000001018", 22, PaymentStatus.Failed, "not_submitted")]
    public async Task UnansweredCharges_AreSettledByReconciliation(string card, int minutes, PaymentStatus status, string? failure)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await WithoutSimulatorNotificationsAsync(client);
        var customer = await CustomerAsync(client);
        await SaveSimulatorCardAsync(host, client, customer, card);
        var invoice = await FinalizedInvoiceAsync(client, customer);
        await host.RunWorkersUntilIdleAsync();
        (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Processing);

        for (var minute = 1; minute <= minutes; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await host.RunWorkersUntilIdleAsync();
        }

        var payment = (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem();
        (payment.Status, payment.FailureCode).ShouldBe((status, failure));
        (await InvoiceAsync(client, invoice)).Status.ShouldBe(status == PaymentStatus.Succeeded ? InvoiceStatus.Paid : InvoiceStatus.Open);
    }

    [Theory]
    [Trait("Requirement", "ENG-15")]
    [InlineData("succeeded", PaymentStatus.Succeeded, null)]
    [InlineData("failed", PaymentStatus.Failed, "card_declined")]
    [InlineData("pending", PaymentStatus.Processing, null)]
    [InlineData("unavailable", PaymentStatus.Processing, null)]
    [InlineData("not_found", PaymentStatus.Processing, null)]
    public async Task Reconciliation_AppliesWhatTheProviderSays(string answer, PaymentStatus status, string? failure)
    {
        var provider = new ScriptedProvider();
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        var (invoice, payment) = await UnansweredChargeAsync(host, client, provider);
        provider.Lookups.Enqueue(answer switch
        {
            "succeeded" => new PaymentLookup.Found(new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded, ProviderPaymentId = "scripted-1" }),
            "failed" => new PaymentLookup.Found(PaymentOutcome.Failure(FailureCode.CardDeclined, "05", "Do not honor")),
            "pending" => new PaymentLookup.Found(new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Pending }),
            "not_found" => new PaymentLookup.NotFound(),
            _ => new PaymentLookup.Unavailable("Timed out."),
        });

        _time.Advance(TimeSpan.FromMinutes(2));
        await host.RunWorkersUntilIdleAsync();
        var settled = await PaymentAsync(client, payment);

        (settled.Status, settled.FailureCode).ShouldBe((status, failure));
        provider.Queries.ShouldHaveSingleItem().Reference.ShouldBe(settled.Reference);
        var reconcileAt = await host.QueryAsync<DateTimeOffset?>($"SELECT reconcile_at AS \"Value\" FROM tahsil.payments WHERE id = {payment}");
        reconcileAt.ShouldBe([status == PaymentStatus.Processing ? FakeBillingClock.Start.AddMinutes(2 + 5) : null], "the second attempt comes 5 minutes after the first");
        if (answer == "failed")
        {
            settled.ProviderFailureCode.ShouldBe("05");
        }

        (await InvoiceAsync(client, invoice)).Status.ShouldBe(status == PaymentStatus.Succeeded ? InvoiceStatus.Paid : InvoiceStatus.Open);
    }

    [Fact]
    [Trait("Requirement", "PAY-16")]
    public async Task ProvidersThatSeeOnlySuccesses_FailAnUnansweredChargeOnceItIsOldEnough()
    {
        var provider = new ScriptedProvider { FindByReference = ReferenceSearch.SuccessfulOnly };
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        var (_, payment) = await UnansweredChargeAsync(host, client, provider);
        provider.Lookups.Enqueue(new PaymentLookup.NotFound());
        provider.Lookups.Enqueue(new PaymentLookup.NotFound());
        provider.Lookups.Enqueue(new PaymentLookup.NotFound());

        _time.Advance(TimeSpan.FromMinutes(7));
        await host.RunWorkersUntilIdleAsync();
        var young = await PaymentAsync(client, payment);
        _time.Advance(TimeSpan.FromMinutes(15));
        await host.RunWorkersUntilIdleAsync();

        young.Status.ShouldBe(PaymentStatus.Processing);
        (await PaymentAsync(client, payment)).FailureCode.ShouldBe("not_submitted");
    }

    [Fact]
    [Trait("Requirement", "ENG-16")]
    public async Task APaymentWithoutAnAnswerForSevenDays_NeedsAnOperator_AndIsGivenUpAfterThirty()
    {
        var provider = new ScriptedProvider();
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        var (_, payment) = await UnansweredChargeAsync(host, client, provider);

        var attempts = new List<DateTimeOffset>();
        for (var hour = 0; hour < 31 * 24; hour++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            var before = provider.Queries.Count;
            await host.RunWorkersUntilIdleAsync();
            if (provider.Queries.Count > before)
            {
                attempts.Add(_time.GetUtcNow());
            }

            if (hour == 6 * 24)
            {
                (await AttentionAsync(host, payment)).ShouldBeEmpty();
            }
        }

        (await AttentionAsync(host, payment)).ShouldBe(["reconciliation_overdue"]);
        (await PaymentAsync(client, payment)).Status.ShouldBe(PaymentStatus.Processing);
        (await host.QueryAsync<DateTimeOffset?>($"SELECT reconcile_at AS \"Value\" FROM tahsil.payments WHERE id = {payment}")).ShouldBe([null]);
        attempts.Count.ShouldBeInRange(30, 40, "hourly at first, then daily");
    }

    [Fact]
    [Trait("Requirement", "ENG-17")]
    [Trait("Requirement", "PAY-17")]
    public async Task ProvidersThatCannotBeSearched_SendUncertainPaymentsToAnOperatorAtOnce()
    {
        var provider = new ScriptedProvider { FindByReference = ReferenceSearch.None };
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        var (_, payment) = await UnansweredChargeAsync(host, client, provider);

        _time.Advance(TimeSpan.FromMinutes(2));
        await host.RunWorkersUntilIdleAsync();

        provider.Queries.ShouldBeEmpty();
        (await AttentionAsync(host, payment)).ShouldBe(["reconciliation_impossible"]);
        (await host.QueryAsync<DateTimeOffset?>($"SELECT reconcile_at AS \"Value\" FROM tahsil.payments WHERE id = {payment}")).ShouldBe([null]);
    }

    [Theory]
    [Trait("Requirement", "ENG-18")]
    [InlineData("not_found", PaymentStatus.Canceled)]
    [InlineData("pending", PaymentStatus.Canceled)]
    [InlineData("succeeded", PaymentStatus.Succeeded)]
    public async Task ACustomersAttemptThatRunsOutOfTime_IsCanceled_UnlessTheProviderHasASuccess(string answer, PaymentStatus status)
    {
        var provider = new ScriptedProvider();
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        await RouteToScriptedAsync(client);
        var customer = await CustomerAsync(client);
        var invoice = await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice);
        var payment = await StartOnPageAsync(host, invoice);
        provider.Lookups.Enqueue(answer switch
        {
            "succeeded" => new PaymentLookup.Found(new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded }),
            "pending" => new PaymentLookup.Found(new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Pending }),
            _ => new PaymentLookup.NotFound(),
        });

        _time.Advance(TimeSpan.FromMinutes(29));
        await host.RunWorkersUntilIdleAsync();
        var waiting = await PaymentAsync(client, payment);
        _time.Advance(TimeSpan.FromMinutes(1));
        await host.RunWorkersUntilIdleAsync();

        waiting.Status.ShouldBe(PaymentStatus.RequiresAction);
        (await PaymentAsync(client, payment)).Status.ShouldBe(status);
    }

    private WebApplicationFactory<Program> Host(ScriptedProvider? provider = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("workers"), _time, services =>
        {
            if (provider is not null)
            {
                services.AddSingleton<IPaymentProvider>(provider);
            }
        });

    /// <summary>
    /// The simulator would settle unanswered charges by notifying itself, at a moment of its own; these tests settle
    /// them through reconciliation only.
    /// </summary>
    private static async Task WithoutSimulatorNotificationsAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { notifications = false } }, options: ApiClients.Json),
        };
        using var response = await client.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>A finalized invoice whose automatic charge got no answer, through the scripted provider.</summary>
    private static async Task<(Guid Invoice, Guid Payment)> UnansweredChargeAsync(WebApplicationFactory<Program> host, HttpClient client, ScriptedProvider provider)
    {
        await RouteToScriptedAsync(client);
        var customer = await CustomerAsync(client);
        var first = await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice);
        var started = await StartOnPageAsync(host, first);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PaymentOutcomes>().ApplyAsync(started, new PaymentOutcome
            {
                Status = Providers.Abstractions.PaymentStatus.Succeeded,
                SavedCard = new SavedCard { Token = "scripted-card", Brand = "visa", Last4 = "4242" },
            }, CancellationToken);
        }

        provider.ChargeOutcome = PaymentOutcome.Unknown("Timed out.");
        var invoice = await FinalizedInvoiceAsync(client, customer);
        await host.RunWorkersUntilIdleAsync();
        var payment = (await PaymentsAsync(client, invoice)).ShouldHaveSingleItem();
        payment.Status.ShouldBe(PaymentStatus.Processing);
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
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        using var routed = await client.PutAsJsonAsync(
            new Uri("/api/v1/provider-connections/routing", UriKind.Relative), new Dictionary<string, string> { ["TRY"] = "scripted-main" }, ApiClients.Json, CancellationToken);
        routed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Saves a simulator card as the customer's default by paying a first invoice on the card page.</summary>
    private static async Task SaveSimulatorCardAsync(WebApplicationFactory<Program> host, HttpClient client, Guid customer, string card) =>
        (await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice), card)).PaymentMethodId.ShouldNotBeNull();

    private static async Task<IReadOnlyList<PaymentResponse>> PaymentsAsync(HttpClient client, Guid invoice) =>
        (await (await client.GetPathAsync($"/api/v1/payments?invoice={invoice}")).ReadAsync<Page<PaymentResponse>>()).Items;

    private static async Task<PaymentResponse> PaymentAsync(HttpClient client, Guid payment) =>
        await (await client.GetPathAsync($"/api/v1/payments/{payment}")).ReadAsync<PaymentResponse>();

    private static async Task<InvoiceResponse> InvoiceAsync(HttpClient client, Guid invoice) =>
        await (await client.GetPathAsync($"/api/v1/invoices/{invoice}")).ReadAsync<InvoiceResponse>();

    private static Task<List<string>> AttentionAsync(WebApplicationFactory<Program> host, Guid payment) =>
        host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items WHERE subject_id = {payment}");

}
