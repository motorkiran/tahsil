using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Billing;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using PaymentStatus = Tahsil.Contracts.PaymentStatus;

namespace Tahsil.Server.Tests.Payments;

/// <summary>Provider notifications: the public endpoint, storage once per event, and the worker that applies them.</summary>
public sealed class ProviderNotificationTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.CustomersWrite, TahsilPermissions.PaymentsRead,
        TahsilPermissions.ConnectionsRead, TahsilPermissions.ConnectionsWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SIM-9")]
    [Trait("Requirement", "PAY-24")]
    public async Task TheSimulatorNotifiesItself_AndTheNotificationSettlesThePayment()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);

        // The customer pays on the page but never comes back to Tahsil.
        var payment = await PayWithoutReturningAsync(host, invoice, "4242424242424242");
        await WaitForNotificationsAsync(host, 1);
        await host.RunWorkersUntilIdleAsync();

        (await GetAsync<PaymentResponse>(client, $"/api/v1/payments/{payment}")).Status.ShouldBe(PaymentStatus.Succeeded);
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).Status.ShouldBe(InvoiceStatus.Paid);
        (await host.QueryAsync<string>($"SELECT status AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe(["processed"]);
        (await host.QueryAsync<string>($"SELECT type AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe(["payment.succeeded"]);
    }

    [Fact]
    [Trait("Requirement", "PAY-23")]
    [Trait("Requirement", "SEC-4")]
    public async Task BadSignatures_AreRejected_AndNothingIsStored()
    {
        await using var host = Host();
        using var client = host.CreateClient();
        var connection = await SimulatorConnectionAsync(host);
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"payment.succeeded","reference":"0123"}""");

        using var forged = await PostAsync(client, connection, body, "t=1,v1=" + new string('0', 64));
        using var unsigned = await PostAsync(client, connection, body, signature: null);
        using var unknown = await PostAsync(client, Guid.CreateVersion7(), body, "t=1,v1=00");

        forged.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await forged.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("Invalid signature");
        unsigned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe([0]);
    }

    [Fact]
    [Trait("Requirement", "PAY-23")]
    [Trait("Requirement", "PAY-25")]
    public async Task RepeatedEvents_AreStoredOnce_AndUnknownReferencesAreIgnored()
    {
        await using var host = Host();
        using var client = host.CreateClient();
        var connection = await SimulatorConnectionAsync(host);
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_42","type":"payment.succeeded","reference":"not-ours"}""");
        var signature = await host.Services.GetRequiredService<SimulatorBank>().SignAsync(connection, 1_791_000_000, body, CancellationToken);

        using var first = await PostAsync(client, connection, body, signature);
        using var again = await PostAsync(client, connection, body, signature);
        await host.RunWorkersUntilIdleAsync();

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("OK");
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.QueryAsync<string>($"SELECT status AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe(["ignored"]);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.payments")).ShouldBe([0], "a notification never creates a payment");
        var raw = await host.QueryAsync<byte[]>($"SELECT raw_body AS \"Value\" FROM tahsil.provider_notifications");
        Encoding.UTF8.GetString(raw.Single()).ShouldNotContain("not-ours", Case.Sensitive, "the raw body is encrypted");
    }

    [Fact]
    [Trait("Requirement", "SEC-4")]
    public async Task BodiesOver64Kilobytes_AreRefused()
    {
        await using var host = Host();
        using var client = host.CreateClient();

        using var response = await PostAsync(client, await SimulatorConnectionAsync(host), new byte[(64 * 1024) + 1], "t=1,v1=00");

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    [Trait("Requirement", "PAY-24")]
    public async Task Hints_AreCheckedWithTheProvider()
    {
        var provider = new ScriptedProvider();
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        var (invoice, payment) = await StartedScriptedPaymentAsync(host, client);
        provider.Lookups.Enqueue(new PaymentLookup.Found(new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded }));

        using var response = await PostAsync(host.CreateClient(), await ConnectionAsync(host, "scripted-main"), Encoding.UTF8.GetBytes(payment.ToString("N")), signature: null);
        await host.RunWorkersUntilIdleAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        provider.Queries.ShouldHaveSingleItem();
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).Status.ShouldBe(InvoiceStatus.Paid);
    }

    [Fact]
    [Trait("Requirement", "PAY-24")]
    public async Task ANotificationThatKeepsFailing_FailsAfterTenAttempts_AndNeedsAnOperator()
    {
        var provider = new ScriptedProvider { FailLookups = true };
        await using var host = Host(provider);
        using var client = await host.CreateClientWithAsync(Permissions);
        var (_, payment) = await StartedScriptedPaymentAsync(host, client);

        using var response = await PostAsync(host.CreateClient(), await ConnectionAsync(host, "scripted-main"), Encoding.UTF8.GetBytes(payment.ToString("N")), signature: null);
        for (var hour = 0; hour < 12; hour++)
        {
            await host.RunWorkersUntilIdleAsync();
            _time.Advance(TimeSpan.FromHours(1));
        }

        (await host.QueryAsync<string>($"SELECT status AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe(["failed"]);
        (await host.QueryAsync<int>($"SELECT attempts::int AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe([10]);
        (await host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items")).ShouldBe(["notification_failed"]);
    }

    [Fact]
    public async Task RawBodies_AreClearedAfter30Days()
    {
        await using var host = Host();
        using var client = host.CreateClient();
        var connection = await SimulatorConnectionAsync(host);
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_7","type":"payment.failed","reference":"x"}""");
        using var response = await PostAsync(client, connection, body, await host.Services.GetRequiredService<SimulatorBank>().SignAsync(connection, 1, body, CancellationToken));

        _time.Advance(TimeSpan.FromDays(31));
        (await host.Services.GetRequiredService<ProviderNotificationCleanup>().RunOnceAsync(CancellationToken)).ShouldBeTrue();

        (await host.QueryAsync<byte[]?>($"SELECT raw_body AS \"Value\" FROM tahsil.provider_notifications")).ShouldBe([null]);
    }

    private WebApplicationFactory<Program> Host(ScriptedProvider? provider = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("notifications"), _time, services =>
        {
            if (provider is not null)
            {
                services.AddSingleton<IPaymentProvider>(provider);
            }
        });

    private static async Task<Guid> PayWithoutReturningAsync(WebApplicationFactory<Program> host, Guid invoice, string card)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var started = (await scope.ServiceProvider.GetRequiredService<Application.Payments.CustomerPayments>().StartAsync(Start(invoice), CancellationToken)).Value;
        var token = System.Text.RegularExpressions.Regex.Match(started.Presentation.ShouldBeOfType<Presentation.Embed>().Html, "simulator/pay/(?<token>[A-Za-z0-9_-]+)").Groups["token"].Value;
        await host.Services.GetRequiredService<SimulatorBank>().SubmitCardAsync(token, new CardEntryForm(card, "12/30", "123", SaveCard: true), CancellationToken);
        return started.Payment.Id;
    }

    private static async Task<(Guid Invoice, Guid Payment)> StartedScriptedPaymentAsync(WebApplicationFactory<Program> host, HttpClient client)
    {
        using (var created = await client.PostJsonAsync("/api/v1/provider-connections", new CreateConnectionRequest
        {
            Key = "scripted-main",
            Provider = ScriptedProvider.Key,
            Name = "Scripted",
            Environment = ConnectionEnvironment.Sandbox,
        }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var routed = await client.PutAsJsonAsync(
            new Uri("/api/v1/provider-connections/routing", UriKind.Relative), new Dictionary<string, string> { ["TRY"] = "scripted-main" }, ApiClients.Json, CancellationToken))
        {
            routed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        return (invoice, await StartOnPageAsync(host, invoice));
    }

    private static Task<Guid> SimulatorConnectionAsync(WebApplicationFactory<Program> host) => ConnectionAsync(host, "simulator");

    private static async Task<Guid> ConnectionAsync(WebApplicationFactory<Program> host, string key) =>
        (await host.QueryAsync<Guid>($"SELECT id AS \"Value\" FROM tahsil.provider_connections WHERE key = {key}")).Single();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid connection, byte[] body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/provider-notifications/{connection}", UriKind.Relative)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation(SimulatorBank.SignatureHeader, signature);
        }

        return await client.SendAsync(request, CancellationToken);
    }

    /// <summary>The simulator notifies in the background; waits until its notifications are stored.</summary>
    private static async Task WaitForNotificationsAsync(WebApplicationFactory<Program> host, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.provider_notifications")).Single() >= count)
            {
                return;
            }

            await Task.Delay(50, CancellationToken);
        }

        throw new TimeoutException("The simulator's notification never arrived.");
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }
}
