using System.Text.Json;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.PayTR;

/// <summary>A PayTR answer: its JSON, read leniently (numbers as strings, <c>returns</c> as <c>""</c> or an array), or no answer.</summary>
internal sealed class PayTRAnswer(JsonDocument? document, bool unanswered) : IDisposable
{
    public bool Unanswered { get; } = unanswered;

    public JsonElement Root => document?.RootElement ?? default;

    public string? Status => document is null ? null : Root.TextAt("status");

    public bool Succeeded => Status == "success";

    public string? ErrorNumber => document is null ? null : Root.TextAt("err_no");

    public string? Message => document is null ? null : Root.TextAt("err_msg") ?? Root.TextAt("reason") ?? Root.TextAt("msg");

    public void Dispose() => document?.Dispose();
}

/// <summary>PayTR's one host, form posts in UTF-8 (§3.1), with the merchant key and salt redacted from logs.</summary>
internal static class PayTRApi
{
    public static readonly Uri Host = new("https://www.paytr.com/");

    private static readonly IReadOnlySet<string> Secrets = Redaction.With("paytr_token", "utoken", "ctoken", "hash");

    public static async Task<PayTRAnswer> PostAsync(
        ProviderContext context, string path, IReadOnlyList<KeyValuePair<string, string>> fields, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Host, path)) { Content = FormEncoding.Content(fields) };
        var result = await ProviderHttp.SendAsync(context, request, timeout, cancellationToken, Secrets);
        return result.Response is { } response ? new PayTRAnswer(LenientJson.TryParse(response.Body), false) : new PayTRAnswer(null, true);
    }
}
