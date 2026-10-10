using System.Net.Http.Json;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Tests.Recorded;

[Trait("Requirement", "FND-53")]
public sealed class RecordedExchangeHandlerTests
{
    private static readonly object Payment = new { amount = "12.50", reference = "pay_1", buyer = new { email = "ayse@example.com" }, items = new[] { new { id = "1" } } };

    [Fact]
    public async Task MatchingRequests_GetTheRecordedAnswers_InOrder()
    {
        using var handler = new RecordedExchangeHandler("samples/payment.json");
        using var client = handler.Client();

        using var created = await client.SendAsync(PaymentRequest(Payment), TestContext.Current.CancellationToken);
        created.Headers.GetValues("X-Request-Id").ShouldBe(["r-1"]);
        (await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("\"paymentId\":\"p-1\"");

        using var refunded = await client.PostAsync(
            "/v1/refunds", FormEncoding.Content([new("payment", "p-1"), new("amount", "5.00")]), TestContext.Current.CancellationToken);
        (await refunded.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("OK");

        await Should.ThrowAsync<TaskCanceledException>(() => client.GetAsync("/v1/payments/p-1", TestContext.Current.CancellationToken));

        handler.AssertComplete();
        handler.Requests.Select(request => request.Path).ShouldBe(["/v1/payments", "/v1/refunds", "/v1/payments/p-1"]);
    }

    [Theory]
    [InlineData("amount", "body.amount: expected \"12.50\", sent \"12.5\"")]
    [InlineData("extra", "body.extra: not expected")]
    [InlineData("missing", "body.buyer: missing")]
    [InlineData("items", "body.items: expected 1 items, sent 2")]
    public async Task ARequestThatDiffers_Fails_AndSaysHow(string change, string difference)
    {
        object body = change switch
        {
            "amount" => new { amount = "12.5", reference = "pay_1", buyer = new { email = "ayse@example.com" }, items = new[] { new { id = "1" } } },
            "extra" => new { amount = "12.50", reference = "pay_1", buyer = new { email = "ayse@example.com" }, items = new[] { new { id = "1" } }, extra = 1 },
            "missing" => new { amount = "12.50", reference = "pay_1", items = new[] { new { id = "1" } } },
            _ => new { amount = "12.50", reference = "pay_1", buyer = new { email = "ayse@example.com" }, items = new[] { new { id = "1" }, new { id = "2" } } },
        };
        using var handler = new RecordedExchangeHandler("samples/payment.json");
        using var client = handler.Client();

        var exception = await Should.ThrowAsync<UnexpectedRequestException>(() => client.SendAsync(PaymentRequest(body), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(difference);
        exception.Message.ShouldContain("Hand-written sample");
        Should.Throw<Exception>(handler.AssertComplete);
    }

    [Fact]
    public async Task MissingHeadersAndQueryValues_Fail()
    {
        using var handler = new RecordedExchangeHandler("samples/payment.json");
        using var client = handler.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments?expand=card&debug=1") { Content = JsonContent.Create(Payment) };

        var exception = await Should.ThrowAsync<UnexpectedRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("header Authorization: missing");
        exception.Message.ShouldContain("query debug: not expected");
    }

    [Fact]
    public async Task RequestsAfterTheLastExchange_Fail()
    {
        using var handler = new RecordedExchangeHandler(RecordedExchanges.Parse("""
            { "source": "test", "exchanges": [ { "request": { "method": "GET", "path": "/ping" }, "response": { "status": 204 } } ] }
            """));
        using var client = handler.Client();

        using var first = await client.GetAsync("/ping", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<UnexpectedRequestException>(() => client.GetAsync("/ping", TestContext.Current.CancellationToken));

        Should.Throw<Exception>(handler.AssertComplete).Message.ShouldContain("came after the last of 1");
    }

    [Fact]
    public void UnusedExchanges_FailTheTest()
    {
        using var handler = new RecordedExchangeHandler("samples/payment.json");

        Should.Throw<Exception>(handler.AssertComplete).Message.ShouldContain("were not used");
    }

    [Theory]
    [InlineData("""{ "exchanges": [] }""")]
    [InlineData("""{ "source": "x", "exchanges": [ { "request": { "method": "GET", "path": "/" } } ] }""")]
    [InlineData("""{ "source": "x", "exchanges": [ { "request": { "method": "GET", "path": "/", "bdy": {} }, "failure": "timeout" } ] }""")]
    public void MalformedRecordings_AreRejected(string json)
    {
        Should.Throw<Exception>(() => RecordedExchanges.Parse(json));
    }

    private static HttpRequestMessage PaymentRequest(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments?expand=card") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", "IYZWSv2 abc");
        return request;
    }
}
