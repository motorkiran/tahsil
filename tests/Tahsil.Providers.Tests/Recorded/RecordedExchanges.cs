using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tahsil.Providers.Tests.Recorded;

/// <summary>
/// A file of provider request/response pairs, replayed in order by <see cref="RecordedExchangeHandler"/> (ADP-16).
/// <code>
/// {
///   "source": "https://docs.example.com/payments#create (or: sandbox, 2026-10-10)",
///   "exchanges": [
///     {
///       "request": {
///         "method": "POST",
///         "path": "/v1/payments",
///         "query": { "expand": "card" },
///         "headers": { "Authorization": "*", "Content-Type": "application/json" },
///         "json": { "amount": "12.50", "conversationId": "*" }
///       },
///       "response": { "status": 200, "headers": { "X-Request-Id": "r-1" }, "json": { "status": "success" } }
///     },
///     { "request": { "method": "GET", "path": "/v1/payments/1" }, "failure": "timeout" }
///   ]
/// }
/// </code>
/// A request matches when its method and path are equal, its query has exactly the listed values, it carries the
/// listed headers, and its body has exactly the listed fields: <c>json</c> compares JSON, <c>form</c> a form body and
/// <c>text</c> anything else. The value <c>"*"</c> matches any value that is present. A response has a JSON or text
/// body; an exchange with <c>failure</c> (<c>timeout</c> or <c>connection_failed</c>) has no answer.
/// </summary>
public sealed record RecordedExchanges(string Source, IReadOnlyList<RecordedExchange> Exchanges)
{
    public const string Any = "*";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Loads <c>Exchanges/{path}</c> from the test output.</summary>
    public static RecordedExchanges Load(string path) =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Exchanges", path)));

    public static RecordedExchanges Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RecordedExchanges>(json, Options) ?? throw new InvalidDataException("The recording is empty.");
        if (string.IsNullOrWhiteSpace(file.Source))
        {
            throw new InvalidDataException("A recording names its source (ADP-16).");
        }

        foreach (var exchange in file.Exchanges)
        {
            if ((exchange.Response is null) == (exchange.Failure is null))
            {
                throw new InvalidDataException($"The exchange for {exchange.Request.Method} {exchange.Request.Path} needs either a response or a failure.");
            }
        }

        return file;
    }
}

public sealed record RecordedExchange(RecordedRequest Request, RecordedResponse? Response, RecordedFailure? Failure);

public sealed record RecordedRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Query = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    JsonNode? Json = null,
    IReadOnlyDictionary<string, string>? Form = null,
    string? Text = null);

public sealed record RecordedResponse(int Status, IReadOnlyDictionary<string, string>? Headers = null, JsonNode? Json = null, string? Text = null);

public enum RecordedFailure
{
    Timeout,
    ConnectionFailed,
}
