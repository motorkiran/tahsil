using System.Security.Cryptography;
using System.Text;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Craftgate;

/// <summary>Craftgate's request signatures (CRG-4) and webhook signatures (CRG-12).</summary>
internal static class CraftgateSigning
{
    /// <summary>Base64 of SHA-256 over the URL-decoded full URL, the API key, the secret key, the random key and the exact body.</summary>
    public static string Request(string url, string apiKey, string secretKey, string randomKey, string? body) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(url) + apiKey + secretKey + randomKey + body)));

    /// <summary>Base64 of HMAC-SHA256 keyed with the webhook key over the event type, timestamp, status and payload ID.</summary>
    public static string Webhook(string webhookKey, string eventType, string eventTimestamp, string status, string payloadId) =>
        Hmac.Base64(HmacAlgorithm.Sha256, webhookKey, eventType + eventTimestamp + status + payloadId);
}
