using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Tahsil.Application.Webhooks;
using Tahsil.Domain.Webhooks;

namespace Tahsil.Infrastructure.Webhooks;

/// <summary>The outcome of one attempt: the endpoint's answer, or why there was none.</summary>
internal sealed record WebhookAttempt(bool Succeeded, int? StatusCode, string? ResponseBody, string? Error);

/// <summary>
/// Sends a delivery as the Standard Webhooks specification describes (EVT-7): the event as the body, and
/// <c>webhook-id</c>, <c>webhook-timestamp</c> and <c>webhook-signature</c> headers, the last an HMAC-SHA256 of the ID,
/// timestamp and body. During a secret rotation the signature header carries both signatures (EVT-8). Any 2xx answer
/// counts as received; redirects are not followed. The timestamp is real time, which receivers compare with their clock.
/// </summary>
internal sealed class WebhookSender(IHttpClientFactory httpClientFactory, TimeProvider timeProvider)
{
    public const string HttpClientName = "Tahsil.Webhooks";

    public async Task<WebhookAttempt> SendAsync(string url, IReadOnlyList<string> secrets, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var id = delivery.EventId.ToString();
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", string.Join(' ', secrets.Select(secret => $"v1,{Sign(secret, id, timestamp, delivery.Payload)}")));

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await ReadStartAsync(response.Content, cancellationToken);
            return new WebhookAttempt(response.IsSuccessStatusCode, (int)response.StatusCode, body, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new WebhookAttempt(false, null, null, exception is TaskCanceledException ? "The endpoint did not answer in time." : exception.Message);
        }
    }

    /// <summary>The signature of a delivery, as receivers compute it to check it.</summary>
    public static string Sign(string secret, string id, string timestamp, string body) =>
        Convert.ToBase64String(HMACSHA256.HashData(WebhookSecrets.KeyOf(secret), Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));

    /// <summary>Enough of the answer to tell what went wrong, without reading a large body.</summary>
    private static async Task<string?> ReadStartAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var buffer = new char[WebhookDelivery.ResponseBodyMaxLength];
        using var reader = new StreamReader(await content.ReadAsStreamAsync(cancellationToken));
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        return read == 0 ? null : new string(buffer, 0, read);
    }
}

/// <summary>The sender's HTTP client: a timeout per attempt, no redirects, no proxy (a proxy would skip the network check, FND-40).</summary>
internal static class WebhookHttpClient
{
    public static void Configure(HttpClient client, TimeSpan timeout)
    {
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tahsil-Webhooks", "1.0"));
    }

    public static SocketsHttpHandler Handler(bool allowPrivateNetworks) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = WebhookNetwork.Connect(allowPrivateNetworks),
    };
}
