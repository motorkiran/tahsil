using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Tests.Recorded;

/// <summary>
/// Replays recorded exchanges in order as an <see cref="HttpMessageHandler"/> (FND-53). A request that differs from the
/// next recorded one, or comes after the last, fails the test: the handler throws, and remembers the mismatch for
/// <see cref="AssertComplete"/> in case the adapter swallowed the exception.
/// </summary>
public sealed class RecordedExchangeHandler(RecordedExchanges recording) : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<string> _mismatches = [];
    private readonly List<CapturedRequest> _requests = [];
    private int _next;

    public RecordedExchangeHandler(string path)
        : this(RecordedExchanges.Load(path))
    {
    }

    /// <summary>The requests the adapter sent, in order.</summary>
    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>A client with this handler, for <see cref="ProviderContext.Http"/>.</summary>
    public HttpClient Client(string baseAddress = "https://provider.test/") => new(this, disposeHandler: false) { BaseAddress = new Uri(baseAddress) };

    /// <summary>Fails unless every recorded exchange was used and every request matched.</summary>
    public void AssertComplete()
    {
        lock (_gate)
        {
            _mismatches.ShouldBeEmpty($"Unexpected requests (source: {recording.Source})");
            _next.ShouldBe(recording.Exchanges.Count, $"Recorded exchanges were not used (source: {recording.Source})");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(
            request.Method.Method,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            FormEncoding.Decode(request.RequestUri?.Query),
            Headers(request),
            body);

        RecordedExchange exchange;
        lock (_gate)
        {
            _requests.Add(captured);
            if (_next >= recording.Exchanges.Count)
            {
                throw Unexpected($"{captured.Method} {captured.Path} came after the last of {recording.Exchanges.Count} recorded exchanges.");
            }

            exchange = recording.Exchanges[_next];
            var differences = Compare(exchange.Request, captured);
            if (differences.Count > 0)
            {
                throw Unexpected($"{captured.Method} {captured.Path} does not match exchange {_next + 1} ({exchange.Request.Method} {exchange.Request.Path}):{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", differences)}");
            }

            _next++;
        }

        switch (exchange.Failure)
        {
            case RecordedFailure.Timeout:
                throw new TaskCanceledException("The recorded request timed out.", new TimeoutException());
            case RecordedFailure.ConnectionFailed:
                throw new HttpRequestException(HttpRequestError.ConnectionError, "The recorded request could not connect.");
        }

        return Response(exchange.Response!);
    }

    private UnexpectedRequestException Unexpected(string message)
    {
        _mismatches.Add(message);
        return new UnexpectedRequestException($"{message}{Environment.NewLine}  (source: {recording.Source})");
    }

    private static Dictionary<string, string> Headers(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        return headers;
    }

    private static List<string> Compare(RecordedRequest expected, CapturedRequest actual)
    {
        var differences = new List<string>();
        if (!string.Equals(expected.Method, actual.Method, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"method: expected {expected.Method}, sent {actual.Method}");
        }

        if (!string.Equals(expected.Path, actual.Path, StringComparison.Ordinal))
        {
            differences.Add($"path: expected {expected.Path}, sent {actual.Path}");
        }

        CompareFields("query", expected.Query ?? new Dictionary<string, string>(), actual.Query, differences);
        foreach (var (name, value) in expected.Headers ?? new Dictionary<string, string>())
        {
            if (!actual.Headers.TryGetValue(name, out var sent))
            {
                differences.Add($"header {name}: missing");
            }
            else if (!Matches(value, sent))
            {
                differences.Add($"header {name}: expected {value}, sent {sent}");
            }
        }

        if (expected.Json is not null)
        {
            JsonNode? sent = null;
            try
            {
                sent = actual.Body is null ? null : JsonNode.Parse(actual.Body);
            }
            catch (System.Text.Json.JsonException)
            {
                differences.Add("body: expected JSON, sent something else");
            }

            CompareJson("body", expected.Json, sent, differences);
        }
        else if (expected.Form is not null)
        {
            CompareFields("form", expected.Form, FormEncoding.Decode(actual.Body), differences);
        }
        else if (expected.Text is not null)
        {
            if (!Matches(expected.Text, actual.Body ?? string.Empty))
            {
                differences.Add($"body: expected {expected.Text}, sent {actual.Body}");
            }
        }
        else if (!string.IsNullOrEmpty(actual.Body))
        {
            differences.Add("body: expected none, sent one");
        }

        return differences;
    }

    private static void CompareFields(string what, IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual, List<string> differences)
    {
        foreach (var (name, value) in expected)
        {
            if (!actual.TryGetValue(name, out var sent))
            {
                differences.Add($"{what} {name}: missing");
            }
            else if (!Matches(value, sent))
            {
                differences.Add($"{what} {name}: expected {value}, sent {sent}");
            }
        }

        foreach (var name in actual.Keys.Except(expected.Keys))
        {
            differences.Add($"{what} {name}: not expected");
        }
    }

    private static void CompareJson(string path, JsonNode? expected, JsonNode? actual, List<string> differences)
    {
        switch (expected, actual)
        {
            case (JsonValue wildcard, not null) when wildcard.TryGetValue<string>(out var text) && text == RecordedExchanges.Any:
                return;
            case (JsonObject expectedObject, JsonObject actualObject):
                foreach (var (name, value) in expectedObject)
                {
                    if (!actualObject.ContainsKey(name))
                    {
                        differences.Add($"{path}.{name}: missing");
                    }
                    else
                    {
                        CompareJson($"{path}.{name}", value, actualObject[name], differences);
                    }
                }

                foreach (var (name, _) in actualObject.Where(property => !expectedObject.ContainsKey(property.Key)))
                {
                    differences.Add($"{path}.{name}: not expected");
                }

                return;
            case (JsonArray expectedArray, JsonArray actualArray):
                if (expectedArray.Count != actualArray.Count)
                {
                    differences.Add($"{path}: expected {expectedArray.Count} items, sent {actualArray.Count}");
                    return;
                }

                for (var index = 0; index < expectedArray.Count; index++)
                {
                    CompareJson($"{path}[{index}]", expectedArray[index], actualArray[index], differences);
                }

                return;
            default:
                if (!JsonNode.DeepEquals(expected, actual))
                {
                    differences.Add($"{path}: expected {expected?.ToJsonString() ?? "null"}, sent {actual?.ToJsonString() ?? "nothing"}");
                }

                return;
        }
    }

    private static bool Matches(string expected, string actual) => expected == RecordedExchanges.Any || expected == actual;

    private static HttpResponseMessage Response(RecordedResponse recorded)
    {
        var response = new HttpResponseMessage((HttpStatusCode)recorded.Status);
        var headers = new Dictionary<string, string>(recorded.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        headers.Remove("Content-Type", out var contentType);
        response.Content = recorded.Json is not null
            ? new StringContent(recorded.Json.ToJsonString(), Encoding.UTF8, contentType ?? "application/json")
            : new StringContent(recorded.Text ?? string.Empty, Encoding.UTF8, contentType ?? "text/plain");
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}

public sealed record CapturedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>A request the recording did not expect; not an <see cref="HttpRequestException"/>, so adapters do not mistake it for a network failure.</summary>
public sealed class UnexpectedRequestException(string message) : Exception(message);
