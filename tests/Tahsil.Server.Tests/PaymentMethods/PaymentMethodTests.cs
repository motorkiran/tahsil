using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;

namespace Tahsil.Server.Tests.PaymentMethods;

/// <summary>Saved cards: the simulator's test API, defaults, removal and expiry.</summary>
public sealed class PaymentMethodTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersRead, TahsilPermissions.CustomersWrite, TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite,
        TahsilPermissions.PaymentsRead, TahsilPermissions.TestWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SIM-7")]
    [Trait("Requirement", "CUS-13")]
    public async Task TheTestApi_SavesSimulatorCards_ThatPayInvoices()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);

        var card = await SaveTestCardAsync(client, customer, "4242424242424242");
        using var notACard = await client.PostJsonAsync("/api/v1/test/simulator/payment-methods", new CreateSimulatorPaymentMethodRequest { Customer = customer, Card = "4111111111111111" });
        var payment = await PayAsync(client, await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice), card.Id);

        (card.Status, card.IsDefault, card.Brand, card.Last4, card.ExpMonth).ShouldBe((PaymentMethodStatus.Active, true, "visa", "4242", 12));
        card.ExpYear.ShouldBe(2030);
        (await notACard.ProblemCodeAsync()).ShouldBe("simulator.not_a_test_card");
        payment.Status.ShouldBe(PaymentStatus.Succeeded);
        (await host.QueryAsync<string>($"SELECT text_shown AS \"Value\" FROM tahsil.consent_records WHERE customer_id = {customer}")).ShouldBe(["Created by the test API"]);
        (await host.QueryAsync<string>($"SELECT data->>'last4' AS \"Value\" FROM tahsil.audit_events WHERE action = 'payment_method.attached'")).ShouldBe(["4242"]);
    }

    [Fact]
    [Trait("Requirement", "SIM-8")]
    public async Task ASavedCardsBehavior_CanBeScripted()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var card = await SaveTestCardAsync(client, customer, "4242424242424242");

        using var scripted = await PatchAsync(client, $"/api/v1/test/simulator/payment-methods/{card.Id}", new { behavior = "insufficient_funds" });
        using var unknown = await PatchAsync(client, $"/api/v1/test/simulator/payment-methods/{card.Id}", new { behavior = "explode" });
        var payment = await PayAsync(client, await FinalizedInvoiceAsync(client, customer, collection: CollectionMethod.SendInvoice), card.Id);

        scripted.StatusCode.ShouldBe(HttpStatusCode.OK, await scripted.Content.ReadAsStringAsync(CancellationToken));
        (await unknown.ProblemCodeAsync()).ShouldBe("simulator.unknown_behavior");
        (payment.Status, payment.FailureCode).ShouldBe((PaymentStatus.Failed, "insufficient_funds"));
    }

    [Fact]
    [Trait("Requirement", "CUS-16")]
    public async Task RemovingACard_ClearsTheDefault_AndDeletesItsTokenAtTheProvider()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var first = await SaveTestCardAsync(client, customer, "4242424242424242");
        var second = await SaveTestCardAsync(client, customer, "9792000000000001");

        using var removed = await client.DeleteAsync(new Uri($"/api/v1/payment-methods/{first.Id}", UriKind.Relative), CancellationToken);
        await host.RunWorkersUntilIdleAsync();
        var cards = await GetAsync<Page<PaymentMethodResponse>>(client, $"/api/v1/customers/{customer}/payment-methods");
        using var asDefault = await client.PostJsonAsync($"/api/v1/customers/{customer}/default-payment-method", new SetDefaultPaymentMethodRequest { PaymentMethod = first.Id });
        var other = await GetAsync<PaymentMethodResponse>(client, $"/api/v1/payment-methods/{second.Id}");
        using var setOther = await client.PostJsonAsync($"/api/v1/customers/{customer}/default-payment-method", new SetDefaultPaymentMethodRequest { PaymentMethod = second.Id });
        var customerAfter = await host.QueryAsync<Guid?>($"SELECT default_payment_method_id AS \"Value\" FROM tahsil.customers WHERE id = {customer}");

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        cards.Items.Select(card => (card.Id, card.Status)).ShouldBe([(second.Id, PaymentMethodStatus.Active), (first.Id, PaymentMethodStatus.Removed)]);
        cards.Items.ShouldAllBe(card => !card.IsDefault, "the removed default leaves none");
        (await asDefault.ProblemCodeAsync()).ShouldBe("payment_method.not_active");
        other.IsDefault.ShouldBeFalse();
        setOther.StatusCode.ShouldBe(HttpStatusCode.OK);
        customerAfter.ShouldBe([second.Id]);
        // One of the simulator's two cards was removed there too; the fake clock gives both the same instant, so no order.
        (await host.QueryAsync<string>($"SELECT state->>'removed' AS \"Value\" FROM tahsil.simulator_transactions WHERE kind = 'card'"))
            .ShouldBe(["false", "true"], ignoreOrder: true);
        (await host.QueryAsync<string>($"SELECT data->>'last4' AS \"Value\" FROM tahsil.audit_events WHERE action = 'payment_method.removed'")).ShouldBe(["4242"]);
    }

    [Fact]
    [Trait("Requirement", "CUS-14")]
    public async Task CardsExpire_AtTheEndOfTheirExpiryMonth()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var card = await SaveTestCardAsync(client, customer, "4242424242424242", expMonth: 10, expYear: 2026);

        _time.Advance(TimeSpan.FromDays(30));
        await host.RunWorkersUntilIdleAsync();
        var lastDay = await GetAsync<PaymentMethodResponse>(client, $"/api/v1/payment-methods/{card.Id}");
        _time.Advance(TimeSpan.FromDays(1));
        await host.RunWorkersUntilIdleAsync();
        var expired = await GetAsync<PaymentMethodResponse>(client, $"/api/v1/payment-methods/{card.Id}");

        card.ExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-TimeSpan.TicksPerMicrosecond));
        lastDay.Status.ShouldBe(PaymentMethodStatus.Active);
        expired.Status.ShouldBe(PaymentMethodStatus.Expired);
    }

    [Theory]
    [Trait("Requirement", "SIM-1")]
    [InlineData("Test", HttpStatusCode.Unauthorized)]
    [InlineData("Live", HttpStatusCode.NotFound)]
    public async Task TheTestApi_ExistsInTestModeOnly(string mode, HttpStatusCode status)
    {
        var settings = new Dictionary<string, string?> { ["Tahsil:Server:Mode"] = mode };
        if (mode == "Live")
        {
            settings["Tahsil:Server:PublicUrl"] = "https://billing.example.com/";
            settings["Tahsil:Server:RequireHttps"] = "true";
        }

        using var host = new TestHost(settings);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/test/simulator/payment-methods", UriKind.Relative), new { customer = Guid.CreateVersion7(), card = "4242424242424242" }, CancellationToken);

        response.StatusCode.ShouldBe(status);
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("cards"), _time);

    private static async Task<PaymentMethodResponse> SaveTestCardAsync(HttpClient client, Guid customer, string card, int? expMonth = null, int? expYear = null)
    {
        using var response = await client.PostJsonAsync("/api/v1/test/simulator/payment-methods", new CreateSimulatorPaymentMethodRequest
        {
            Customer = customer,
            Card = card,
            ExpMonth = expMonth,
            ExpYear = expYear,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<PaymentMethodResponse>();
    }

    private static async Task<PaymentResponse> PayAsync(HttpClient client, Guid invoice, Guid card)
    {
        using var response = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { PaymentMethod = card });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<PaymentResponse>();
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) }, CancellationToken);

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }
}
