using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Webhooks;

namespace Tahsil.Server.Tests.Webhooks;

public sealed class WebhookTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.WebhooksRead, TahsilPermissions.WebhooksWrite, TahsilPermissions.CustomersWrite,
        TahsilPermissions.InvoicesWrite, TahsilPermissions.InvoicesRead,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);
    private readonly TestWebhookReceiver _receiver = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "EVT-7")]
    public void Signatures_FollowTheStandardWebhooksVector()
    {
        // The example of the Standard Webhooks specification.
        WebhookSender.Sign("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", "msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", """{"test": 2432232314}""")
            .ShouldBe("g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=");
    }

    [Fact]
    [Trait("Requirement", "EVT-1")]
    [Trait("Requirement", "EVT-4")]
    [Trait("Requirement", "EVT-9")]
    public async Task Endpoints_ReceiveSignedEvents_ThatAreTheAuditEventsOfTheirChange()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var url = _receiver.NewEndpoint();
        var created = await CreateEndpointAsync(client, url, [TahsilEventTypes.CustomerCreated]);

        var customer = await CreateCustomerAsync(client, "user-42");
        await host.RunWorkersUntilIdleAsync();
        await host.RunWorkersUntilIdleAsync();

        var received = _receiver.ReceivedBy(url).ShouldHaveSingleItem();
        var auditId = (await host.QueryAsync<Guid>($"SELECT id AS \"Value\" FROM tahsil.audit_events WHERE action = 'customer.created'")).Single();
        received.Id.ShouldBe(auditId.ToString());
        received.Headers["webhook-signature"].ToString().ShouldBe($"v1,{WebhookSender.Sign(created.Secret, received.Id, received.Headers["webhook-timestamp"]!, received.Body)}");
        received.Headers["User-Agent"].ToString().ShouldBe("Tahsil-Webhooks/1.0");
        var body = JsonNode.Parse(received.Body)!;
        (body["type"]!.GetValue<string>(), body["mode"]!.GetValue<string>()).ShouldBe(("customer.created", "test"));
        body["data"]!["subjectType"]!.GetValue<string>().ShouldBe("customer");
        body["data"]!["subjectId"]!.GetValue<string>().ShouldBe(customer.ToString());
        body["data"]!["customerId"]!.GetValue<Guid>().ShouldBe(customer);
        body["data"]!["actor"]!["type"]!.GetValue<string>().ShouldBe("api_key");
        body["data"]!["details"]!["externalId"]!.GetValue<string>().ShouldBe("user-42");
    }

    [Fact]
    [Trait("Requirement", "EVT-11")]
    [Trait("Requirement", "EVT-14")]
    public async Task FailedDeliveries_AreRetried_WithTheSameId_GiveUpAfterNineAttempts_AndCanBeSentAgain()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var url = _receiver.NewEndpoint(HttpStatusCode.InternalServerError);
        var endpoint = await CreateEndpointAsync(client, url, []);
        await CreateCustomerAsync(client, "user-1");
        await host.RunWorkersUntilIdleAsync();

        _time.Advance(TimeSpan.FromSeconds(10));
        await host.RunWorkersUntilIdleAsync();
        var retried = _receiver.ReceivedBy(url);

        retried.Select(webhook => webhook.Answer).ShouldBe([HttpStatusCode.InternalServerError, HttpStatusCode.OK]);
        retried.Select(webhook => webhook.Id).Distinct().ShouldHaveSingleItem();

        var failing = _receiver.NewEndpoint();
        _receiver.AnswerWith(failing, HttpStatusCode.ServiceUnavailable);
        var failingEndpoint = await CreateEndpointAsync(client, failing, []);
        await CreateCustomerAsync(client, "user-2");
        await host.RunWorkersUntilIdleAsync();
        for (var hour = 0; hour < 50; hour++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            await host.RunWorkersUntilIdleAsync();
        }

        var deliveries = await GetAsync<Page<WebhookDeliveryResponse>>(client, $"/api/v1/webhooks/deliveries?endpoint={failingEndpoint.Endpoint.Id}&status=failed");
        var delivery = deliveries.Items.ShouldHaveSingleItem();
        (delivery.Attempts, delivery.ResponseStatusCode, delivery.ResponseBody).ShouldBe((9, 503, "not now"));
        _receiver.ReceivedBy(failing).Count.ShouldBe(9);
        (await GetAsync<WebhookEndpointResponse>(client, $"/api/v1/webhooks/endpoints/{failingEndpoint.Endpoint.Id}")).FailingSince.ShouldNotBeNull();

        _receiver.AnswerWith(failing, HttpStatusCode.OK);
        using var redeliver = await client.PostPathAsync($"/api/v1/webhooks/deliveries/{delivery.Id}/redeliver");
        await host.RunWorkersUntilIdleAsync();

        redeliver.StatusCode.ShouldBe(HttpStatusCode.OK);
        _receiver.ReceivedBy(failing)[^1].Id.ShouldBe(delivery.EventId.ToString());
        (await GetAsync<WebhookEndpointResponse>(client, $"/api/v1/webhooks/endpoints/{failingEndpoint.Endpoint.Id}")).FailingSince.ShouldBeNull();
        endpoint.Endpoint.Enabled.ShouldBeTrue();
    }

    [Fact]
    [Trait("Requirement", "EVT-9")]
    public async Task Endpoints_ReceiveOnlyTheirEvents_AndNothingWhileDisabled_ExceptTests()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var invoicesOnly = _receiver.NewEndpoint();
        var disabled = _receiver.NewEndpoint();
        await CreateEndpointAsync(client, invoicesOnly, [TahsilEventTypes.InvoiceFinalized]);
        var off = await CreateEndpointAsync(client, disabled, [], enabled: false);

        var customer = await CreateCustomerAsync(client, "user-1");
        await FinalizeInvoiceAsync(client, customer);
        using var test = await client.PostPathAsync($"/api/v1/webhooks/endpoints/{off.Endpoint.Id}/test");
        await host.RunWorkersUntilIdleAsync();
        await host.RunWorkersUntilIdleAsync();

        _receiver.ReceivedBy(invoicesOnly).Select(webhook => JsonNode.Parse(webhook.Body)!["type"]!.GetValue<string>()).ShouldBe(["invoice.finalized"]);
        _receiver.ReceivedBy(disabled).Select(webhook => JsonNode.Parse(webhook.Body)!["type"]!.GetValue<string>()).ShouldBe(["webhook.test"]);
        var finalized = JsonNode.Parse(_receiver.ReceivedBy(invoicesOnly)[0].Body)!["data"]!["details"]!;
        (finalized["total"]!.GetValue<long>(), finalized["currency"]!.GetValue<string>()).ShouldBe((12_500, "TRY"));
        finalized["number"]!.GetValue<string>().ShouldStartWith("INV-");
    }

    [Fact]
    [Trait("Requirement", "EVT-8")]
    public async Task ARotatedSecret_KeepsSigningBesideTheNewOne_ForTheOverlap()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var url = _receiver.NewEndpoint();
        var created = await CreateEndpointAsync(client, url, []);

        var rotated = await PostAsync<WebhookSecretResponse>(client, $"/api/v1/webhooks/endpoints/{created.Endpoint.Id}/secret");
        await CreateCustomerAsync(client, "during-overlap");
        await host.RunWorkersUntilIdleAsync();
        _time.Advance(TimeSpan.FromHours(25));
        await CreateCustomerAsync(client, "after-overlap");
        await host.RunWorkersUntilIdleAsync();

        var (during, after) = (_receiver.ReceivedBy(url)[0], _receiver.ReceivedBy(url)[1]);
        rotated.PreviousSecretExpiresAt.ShouldBe(FakeBillingClock.Start.AddHours(24));
        during.Headers["webhook-signature"].ToString().ShouldBe(
            $"v1,{Signature(rotated.Secret, during)} v1,{Signature(created.Secret, during)}");
        after.Headers["webhook-signature"].ToString().ShouldBe($"v1,{Signature(rotated.Secret, after)}");
    }

    [Fact]
    [Trait("Requirement", "EVT-10")]
    public async Task OneEndpoint_GetsAtMostItsConcurrency_AtOnce()
    {
        await using var host = Host(new() { ["Tahsil:Webhooks:EndpointConcurrency"] = "2" });
        using var client = await host.CreateClientWithAsync(Permissions);
        var url = _receiver.NewEndpoint();
        await CreateEndpointAsync(client, url, [TahsilEventTypes.CustomerCreated]);
        for (var index = 0; index < 8; index++)
        {
            await CreateCustomerAsync(client, $"user-{index}");
        }

        _receiver.Delay = TimeSpan.FromMilliseconds(100);
        await host.RunWorkersUntilIdleAsync();
        await host.RunWorkersUntilIdleAsync();

        _receiver.ReceivedBy(url).Count.ShouldBe(8);
        _receiver.MaxInFlight.ShouldBe(2);
    }

    [Theory]
    [Trait("Requirement", "EVT-12")]
    [InlineData("http://example.com/hooks", "webhook_endpoint.invalid_url")]
    [InlineData("https://user:password@example.com/hooks", "webhook_endpoint.invalid_url")]
    [InlineData("https://example.com/hooks#fragment", "webhook_endpoint.invalid_url")]
    [InlineData("https://example.com/hooks", "webhook_endpoint.unknown_event_type")]
    public async Task Endpoints_NeedSecureUrls_AndKnownEvents(string url, string code)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);

        using var response = await client.PostJsonAsync("/api/v1/webhooks/endpoints", new CreateWebhookEndpointRequest
        {
            Url = url,
            EventTypes = code.EndsWith("unknown_event_type", StringComparison.Ordinal) ? ["customer.exploded"] : [],
        });

        (await response.ProblemCodeAsync()).ShouldBe(code);
    }

    [Fact]
    [Trait("Requirement", "EVT-12")]
    public async Task LoopbackHttp_IsAcceptedInTestModeOnly_AndTheCatalogIsListed()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);

        var local = await CreateEndpointAsync(client, "http://localhost:5000/hooks", []);
        var types = await GetAsync<Page<EventTypeResponse>>(client, "/api/v1/webhooks/event-types");

        local.Secret.ShouldStartWith("whsec_");
        Convert.FromBase64String(local.Secret["whsec_".Length..]).Length.ShouldBe(32);
        types.Items.Select(type => type.Type).ShouldBe(TahsilEventTypes.All.Order(StringComparer.Ordinal));
        types.Items.ShouldAllBe(type => type.Description.Length > 0);
    }

    [Theory]
    [Trait("Requirement", "FND-40")]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.100.200", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2002:a00:1::1", false)]
    [InlineData("2002:a9fe:a9fe::", false)]
    [InlineData("93.184.215.14", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:808:808::1", true)]
    public void OnlyPublicAddresses_ReceiveWebhooks(string address, bool isPublic) =>
        WebhookNetwork.IsPublic(IPAddress.Parse(address)).ShouldBe(isPublic);

    [Fact]
    [Trait("Requirement", "FND-40")]
    public async Task TheSender_RefusesToConnectToThisMachine_AndUsesNoProxy()
    {
        using var handler = WebhookHttpClient.Handler(allowPrivateNetworks: false);
        using var client = new HttpClient(handler, disposeHandler: false);

        var failure = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://localhost:9/"), CancellationToken));

        failure.Message.ShouldContain("private network");
        (handler.UseProxy, handler.AllowAutoRedirect).ShouldBe((false, false));
    }

    private static string Signature(string secret, ReceivedWebhook webhook) => WebhookSender.Sign(secret, webhook.Id, webhook.Headers["webhook-timestamp"]!, webhook.Body);

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>(settings ?? [])
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("webhooks"),
            ["Tahsil:Workers:Enabled"] = "false",
        };
        return server.WithSettings(all, services =>
        {
            services.AddSingleton<BillingClock>(new FakeBillingClock(_time));
            services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver.CreateHandler());
        });
    }

    private static async Task<CreatedWebhookEndpointResponse> CreateEndpointAsync(HttpClient client, string url, IReadOnlyList<string> eventTypes, bool enabled = true)
    {
        using var response = await client.PostJsonAsync("/api/v1/webhooks/endpoints", new CreateWebhookEndpointRequest { Url = url, EventTypes = eventTypes, Enabled = enabled });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<CreatedWebhookEndpointResponse>();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client, string externalId)
    {
        using var response = await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { ExternalId = externalId, Email = $"{externalId}@example.com" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.ReadAsync<CustomerResponse>()).Id;
    }

    private static async Task FinalizeInvoiceAsync(HttpClient client, Guid customer)
    {
        using var draft = await client.PostJsonAsync("/api/v1/invoices", new CreateInvoiceRequest
        {
            Customer = customer,
            Currency = "TRY",
            CollectionMethod = CollectionMethod.SendInvoice,
            Lines = [new InvoiceLineRequest { Description = "Consulting", UnitAmount = 12_500 }],
        });
        using var finalized = await client.PostPathAsync($"/api/v1/invoices/{(await draft.ReadAsync<InvoiceResponse>()).Id}/finalize");
        finalized.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path)
    {
        using var response = await client.PostPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }
}
