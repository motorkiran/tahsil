using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Iyzico;

/// <summary>An iyzico answer: the parsed body of an answered call, or why there is none.</summary>
internal sealed record IyzicoAnswer(JsonDocument? Document, ProviderHttpResponse? Response, ProviderCallFailure? Failure) : IDisposable
{
    public JsonElement Root => Document!.RootElement;

    /// <summary>Whether iyzico answered with <c>status: success</c>; it answers failures with HTTP 200 too (IYZ-3).</summary>
    public bool Succeeded => Document is not null && Root.TextAt("status") == "success";

    /// <summary>No usable answer: no response, a server error, or a body that is not JSON.</summary>
    public bool Unanswered => Document is null || Response is { IsServerError: true };

    public string? ErrorCode => Document is null ? null : Root.TextAt("errorCode");

    public string? ErrorMessage => Document is null ? null : Root.TextAt("errorMessage");

    public string? ErrorGroup => Document is null ? null : Root.TextAt("errorGroup");

    public void Dispose() => Document?.Dispose();
}

/// <summary>Calls iyzico's API with IYZWSv2 authorization (IYZ-2), once, with the operation's timeout (§7).</summary>
internal static class IyzicoApi
{
    /// <summary>Fields whose values never reach the logs.</summary>
    public static readonly IReadOnlySet<string> Redacted = Redaction.With("cardUserKey", "cardToken", "identityNumber", "gsmNumber", "email", "token");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Uri BaseUrl(ProviderEnvironment environment) =>
        new(environment == ProviderEnvironment.Production ? "https://api.iyzipay.com/" : "https://sandbox-api.iyzipay.com/");

    public static Task<IyzicoAnswer> PostAsync(ProviderContext context, string path, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(context, HttpMethod.Post, path, null, body, timeout, cancellationToken);

    public static Task<IyzicoAnswer> DeleteAsync(ProviderContext context, string path, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(context, HttpMethod.Delete, path, null, body, timeout, cancellationToken);

    public static Task<IyzicoAnswer> GetAsync(ProviderContext context, string path, string query, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(context, HttpMethod.Get, path, query, null, timeout, cancellationToken);

    /// <summary>The body without <c>null</c> members: iyzico takes absent optional fields, not empty ones.</summary>
    private static JsonNode Pruned(JsonNode node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var name in members.Where(member => member.Value is null).Select(member => member.Key).ToList())
                {
                    members.Remove(name);
                }

                foreach (var (_, value) in members)
                {
                    Pruned(value!);
                }

                break;
            case JsonArray items:
                foreach (var item in items.OfType<JsonNode>())
                {
                    Pruned(item);
                }

                break;
        }

        return node;
    }

    private static async Task<IyzicoAnswer> SendAsync(
        ProviderContext context, HttpMethod method, string path, string? query, JsonObject? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var text = body is null ? string.Empty : Pruned(body).ToJsonString(Json);
        using var request = new HttpRequestMessage(method, new Uri(BaseUrl(context.Environment), query is null ? path : $"{path}?{query}"));
        request.Headers.TryAddWithoutValidation(
            "Authorization", IyzicoSigning.Authorization(context.Credential("apiKey"), context.Credential("secretKey"), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), path, text));
        if (body is not null)
        {
            request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        }

        var result = await ProviderHttp.SendAsync(context, request, timeout, cancellationToken, Redacted);
        return new IyzicoAnswer(LenientJson.TryParse(result.Response?.Body), result.Response, result.Failure);
    }
}
