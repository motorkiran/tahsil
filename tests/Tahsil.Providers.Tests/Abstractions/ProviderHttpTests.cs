using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Tests.Recorded;

namespace Tahsil.Providers.Tests.Abstractions;

public sealed class ProviderHttpTests
{
    [Fact]
    [Trait("Requirement", "ADP-9")]
    public async Task Answers_AreReadInFull_AndLoggedRedacted_AtDebug()
    {
        using var handler = new RecordedExchangeHandler(RecordedExchanges.Parse("""
            {
              "source": "test",
              "exchanges": [
                {
                  "request": { "method": "POST", "path": "/pay", "json": { "apiKey": "secret-key", "amount": "1.00" } },
                  "response": { "status": 502, "json": { "status": "failure", "email": "ayse@example.com" } }
                }
              ]
            }
            """));
        var logger = new ListLogger();
        var context = Context(handler, logger);

        var result = await ProviderHttp.SendAsync(
            context, new HttpRequestMessage(HttpMethod.Post, "/pay") { Content = JsonContent.Create(new { apiKey = "secret-key", amount = "1.00" }) },
            ProviderHttp.ChangeTimeout, TestContext.Current.CancellationToken);

        result.Response.ShouldNotBeNull().IsServerError.ShouldBeTrue();
        result.Response.Body.ShouldContain("ayse@example.com");
        logger.Lines.ShouldBe([
            """Debug: Provider request POST /pay: {"apiKey":"***","amount":"1.00"}""",
            """Debug: Provider response 502: {"status":"failure","email":"***"}""",
        ]);
        handler.AssertComplete();
    }

    [Theory]
    [Trait("Requirement", "ADP-4")]
    [InlineData("timeout", ProviderCallFailure.Timeout)]
    [InlineData("connection_failed", ProviderCallFailure.ConnectionFailed)]
    public async Task CallsWithoutAnAnswer_AreResults_NotExceptions(string failure, ProviderCallFailure expected)
    {
        using var handler = new RecordedExchangeHandler(RecordedExchanges.Parse($$"""
            { "source": "test", "exchanges": [ { "request": { "method": "GET", "path": "/payments/1" }, "failure": "{{failure}}" } ] }
            """));

        var result = await ProviderHttp.SendAsync(
            Context(handler, new ListLogger()), new HttpRequestMessage(HttpMethod.Get, "/payments/1"), ProviderHttp.LookupTimeout, TestContext.Current.CancellationToken);

        result.Answered.ShouldBeFalse();
        result.Failure.ShouldBe(expected);
    }

    [Fact]
    [Trait("Requirement", "ADP-10")]
    public async Task TheOperationTimeout_EndsASlowCall()
    {
        var time = new FakeTimeProvider();
        using var slow = new SlowHandler();
        using var client = new HttpClient(slow) { BaseAddress = new Uri("https://provider.test/") };
        var context = Context(client, new ListLogger(), time);

        var call = ProviderHttp.SendAsync(context, new HttpRequestMessage(HttpMethod.Get, "/payments/1"), ProviderHttp.LookupTimeout, TestContext.Current.CancellationToken);
        await slow.Started.Task;
        time.Advance(ProviderHttp.LookupTimeout);

        (await call).Failure.ShouldBe(ProviderCallFailure.Timeout);
    }

    [Fact]
    public async Task TheCallersCancellation_StillThrows()
    {
        using var slow = new SlowHandler();
        using var client = new HttpClient(slow) { BaseAddress = new Uri("https://provider.test/") };
        using var cancellation = new CancellationTokenSource();

        var call = ProviderHttp.SendAsync(Context(client, new ListLogger()), new HttpRequestMessage(HttpMethod.Get, "/x"), ProviderHttp.LookupTimeout, cancellation.Token);
        await slow.Started.Task;
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => call);
    }

    private static ProviderContext Context(RecordedExchangeHandler handler, ILogger logger) => Context(handler.Client(), logger);

    private static ProviderContext Context(HttpClient client, ILogger logger, TimeProvider? time = null) => new()
    {
        ConnectionId = Guid.CreateVersion7(),
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string>(),
        Settings = new Dictionary<string, string>(),
        Http = client,
        Time = time ?? TimeProvider.System,
        Logger = logger,
        NotificationUrl = new Uri("https://billing.example.com/webhooks/providers/x"),
    };

    private sealed class SlowHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
