using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Craftgate;

/// <summary>A Craftgate answer (CRG-5): its <c>data</c>, its error, or no answer (timeout, connection, 5xx).</summary>
internal sealed class CraftgateAnswer(int status, JsonDocument? document, bool unanswered) : IDisposable
{
    public int Status { get; } = status;

    public bool Unanswered { get; } = unanswered || status >= 500;

    public bool Succeeded => !Unanswered && Status is >= 200 and < 300;

    public JsonElement Data => document is not null && document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out var data) ? data : default;

    public string? ErrorCode => Error("errorCode");

    public string? ErrorGroup => Error("errorGroup");

    public string? ErrorDescription => Error("errorDescription");

    public string? Message => Unanswered ? "Craftgate did not answer." : ErrorDescription ?? ErrorCode;

    public void Dispose() => document?.Dispose();

    private string? Error(string name) => document is not null && document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.TextAt("errors", name) : null;
}

/// <summary>Signed JSON calls to Craftgate (CRG-3 to CRG-5), with the idempotency key on payments and refunds.</summary>
internal static class CraftgateApi
{
    public static readonly TimeSpan PaymentTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);

    private const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly IReadOnlySet<string> Secrets = Redaction.With("cardUserKey", "cardToken", "x-signature");

    public static Uri BaseUrl(ProviderContext context) =>
        new(context.Environment == ProviderEnvironment.Production ? "https://api.craftgate.io/" : "https://sandbox-api.craftgate.io/");

    public static async Task<CraftgateAnswer> SendAsync(
        ProviderContext context, HttpMethod method, string pathAndQuery, JsonObject? body, TimeSpan timeout, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var url = new Uri(BaseUrl(context), pathAndQuery);
        var json = body?.ToJsonString();
        var randomKey = RandomNumberGenerator.GetString(Alphanumeric, 16);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = json is null ? null : new StringContent(json, new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }),
        };
        var apiKey = context.Credential("apiKey");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("x-rnd-key", randomKey);
        request.Headers.Add("x-auth-version", "v1");
        request.Headers.Add("x-signature", CraftgateSigning.Request(url.AbsoluteUri, apiKey, context.Credential("secretKey"), randomKey, json));
        request.Headers.Add("lang", "tr");
        if (idempotencyKey is not null)
        {
            request.Headers.Add("x-idempotency-key", idempotencyKey);
        }

        var result = await ProviderHttp.SendAsync(context, request, timeout, cancellationToken, Secrets);
        return result.Response is { } response
            ? new CraftgateAnswer(response.Status, LenientJson.TryParse(response.Body), false)
            : new CraftgateAnswer(0, null, true);
    }

    /// <summary>A request amount in major units with exactly two fraction digits (CRG-13).</summary>
    public static JsonNode Price(Amount amount) => JsonValue.Create(decimal.Round(AmountFormat.ToDecimal(amount.MinorUnits, amount.Exponent), 2) + 0.00m)!;

    /// <summary>A date filter in Istanbul local time, without an offset, as Craftgate's filters take them.</summary>
    public static string LocalDate(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public static string Query(params (string Name, string Value)[] values) =>
        string.Join('&', values.Select(value => $"{value.Name}={Uri.EscapeDataString(value.Value)}"));
}
