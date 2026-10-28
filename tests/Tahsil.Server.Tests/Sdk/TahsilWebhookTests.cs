using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.AspNetCore;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Webhooks;

namespace Tahsil.Server.Tests.Sdk;

/// <summary><c>Tahsil.AspNetCore</c> (SDK-10 to SDK-12), with signatures made the way the server makes them.</summary>
public sealed class TahsilWebhookTests(TahsilServerFixture server)
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string Rotated = "whsec_Ln5h3a0bEb8JmBXwCDuTRkzPq6hXa0hV";
    private const string Body = """{"type":"invoice.paid","timestamp":"2026-10-01T09:00:00+00:00","mode":"test","data":{"subjectType":"invoice","subjectId":"01a0f6b1-2680-7c0c-af28-95836928562e","customerId":null,"actor":{"type":"system","id":null},"details":{"number":"INV-2026-000001","total":12500,"amountDue":12500,"amountPaid":12500,"amountRemaining":0,"currency":"TRY","dueDate":null,"subscriptionId":null,"billingReason":"manual"}}}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SDK-10")]
    public void AWebhook_IsVerified_WithAnyOfItsSecrets_AndRefusedWhenStaleOrChanged()
    {
        var time = new FakeTimeProvider(Now);
        var signed = Headers("msg_1", Now, Secret);
        var duringRotation = Headers("msg_2", Now, Rotated, Secret);

        TahsilWebhook.TryVerify(signed, Body, [Secret], time, out var verified).ShouldBeTrue();
        (verified!.Id, verified.Type, verified.Mode, verified.Data.SubjectType).ShouldBe(("msg_1", TahsilEventTypes.InvoicePaid, "test", "invoice"));
        verified.DetailsAs<InvoiceDetails>()!.ShouldSatisfyAllConditions(
            details => details.Number.ShouldBe("INV-2026-000001"),
            details => details.AmountPaid.ShouldBe(12_500),
            details => details.BillingReason.ShouldBe(BillingReason.Manual));
        TahsilWebhook.TryVerify(duringRotation, Body, [Secret], time, out _).ShouldBeTrue("one of the signatures matches the old secret");
        TahsilWebhook.TryVerify(duringRotation, Body, [Rotated], time, out _).ShouldBeTrue("one of the signatures matches the new secret");
        TahsilWebhook.TryVerify(signed, Body, [Rotated, Secret], time, out _).ShouldBeTrue("a receiver may hold both secrets too");

        TahsilWebhook.TryVerify(signed, Body, [Rotated], time, out _).ShouldBeFalse("another secret");
        TahsilWebhook.TryVerify(signed, Body.Replace("12500", "1", StringComparison.Ordinal), [Secret], time, out _).ShouldBeFalse("a changed body");
        TahsilWebhook.TryVerify(Headers("msg_3", Now.AddMinutes(-6), Secret), Body, [Secret], time, out _).ShouldBeFalse("an old timestamp");
        TahsilWebhook.TryVerify(Headers("msg_4", Now.AddMinutes(6), Secret), Body, [Secret], time, out _).ShouldBeFalse("a timestamp from the future");
        TahsilWebhook.TryVerify(new HeaderDictionary(), Body, [Secret], time, out _).ShouldBeFalse("no headers");
        Should.Throw<ArgumentException>(() => TahsilWebhook.TryVerify(signed, Body, [string.Empty], time, out _)).ParamName.ShouldBe("secret");
    }

    [Fact]
    [Trait("Requirement", "SDK-11")]
    [Trait("Requirement", "SDK-12")]
    public async Task TheEndpoint_RefusesWhatItCannotVerify_AndHandlesEachWebhookOnce()
    {
        var handled = new List<string>();
        var failOnce = true;
        await using var app = await ReceiverAsync(async (webhookEvent, _) =>
        {
            if (webhookEvent.Id == "msg_fails" && failOnce)
            {
                failOnce = false;
                throw new InvalidOperationException("The handler failed.");
            }

            handled.Add(webhookEvent.Id);
            await Task.CompletedTask;
        });
        using var client = app.GetTestClient();
        var now = DateTimeOffset.UtcNow;

        using var first = await PostAsync(client, Headers("msg_1", now, Secret), Body);
        using var again = await PostAsync(client, Headers("msg_1", now, Secret), Body);
        using var forged = await PostAsync(client, Headers("msg_2", now, Rotated), Body);
        await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(client, Headers("msg_fails", now, Secret), Body));
        using var retried = await PostAsync(client, Headers("msg_fails", now, Secret), Body);

        (first.StatusCode, again.StatusCode, forged.StatusCode, retried.StatusCode).ShouldBe((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.OK));
        handled.ShouldBe(["msg_1", "msg_fails"], "a redelivery is skipped; a delivery whose handler failed is handled when Tahsil retries it");
    }

    [Fact]
    [Trait("Requirement", "SDK-11")]
    public async Task TahsilsOwnDeliveries_ReachTheHandler_Typed()
    {
        // The application receives through its test server; it starts once Tahsil gave the endpoint its secret.
        WebApplication? receiver = null;
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("sdk_webhooks"), new FakeTimeProvider(DateTimeOffset.UtcNow), services =>
            services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver!.GetTestServer().CreateHandler()));
        using var api = await host.CreateClientWithAsync(TahsilPermissions.WebhooksWrite, TahsilPermissions.CustomersWrite);
        using var endpoint = await api.PostJsonAsync("/api/v1/webhooks/endpoints", new CreateWebhookEndpointRequest { Url = "https://app.example.com/webhooks/tahsil" });
        endpoint.StatusCode.ShouldBe(HttpStatusCode.Created, await endpoint.Content.ReadAsStringAsync(CancellationToken));
        var received = new List<TahsilEvent>();
        receiver = await ReceiverAsync((webhookEvent, _) =>
        {
            received.Add(webhookEvent);
            return Task.CompletedTask;
        }, (await endpoint.ReadAsync<CreatedWebhookEndpointResponse>()).Secret);
        await using var application = receiver;

        using (var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada", Email = "ada@example.com", ExternalId = "user-42" }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await host.RunWorkersUntilIdleAsync();

        var webhookEvent = received.ShouldHaveSingleItem();
        webhookEvent.Type.ShouldBe(TahsilEventTypes.CustomerCreated);
        webhookEvent.DetailsAs<CustomerCreatedDetails>()!.ExternalId.ShouldBe("user-42");
    }

    private static async Task<WebApplication> ReceiverAsync(Func<TahsilEvent, CancellationToken, Task> handler, string secret = Secret)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTahsilWebhooks();
        var app = builder.Build();
        app.MapTahsilWebhooks("/webhooks/tahsil", secret, handler);
        await app.StartAsync(CancellationToken);
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, IHeaderDictionary headers, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/tahsil") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value.ToString());
        }

        return await client.SendAsync(request, CancellationToken);
    }

    /// <summary>The headers Tahsil sends, signed with each secret as the server's sender signs.</summary>
    private static HeaderDictionary Headers(string id, DateTimeOffset at, params string[] secrets)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return new HeaderDictionary
        {
            ["webhook-id"] = id,
            ["webhook-timestamp"] = timestamp,
            ["webhook-signature"] = string.Join(' ', secrets.Select(secret => $"v1,{WebhookSender.Sign(secret, id, timestamp, Body)}")),
        };
    }
}
