using System.Globalization;
using System.Text;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Iyzico;

/// <summary>iyzico's signatures: IYZWSv2 request authorization (IYZ-2), response signatures (IYZ-4) and V3 notifications (IYZ-12).</summary>
internal static class IyzicoSigning
{
    /// <summary>The <c>Authorization</c> header of a request.</summary>
    /// <param name="apiKey">The connection's API key.</param>
    /// <param name="secretKey">The connection's secret key.</param>
    /// <param name="randomKey">A new random value per request.</param>
    /// <param name="uriPath">The path, without host or query string.</param>
    /// <param name="body">The exact JSON sent; empty for a GET.</param>
    public static string Authorization(string apiKey, string secretKey, string randomKey, string uriPath, string body)
    {
        var signature = Hmac.Hex(HmacAlgorithm.Sha256, secretKey, randomKey + uriPath + body);
        return "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"apiKey:{apiKey}&randomKey:{randomKey}&signature:{signature}"));
    }

    /// <summary>Whether a response's <c>signature</c> covers its values, joined with <c>:</c> in the documented order (IYZ-4).</summary>
    public static bool ResponseMatches(string secretKey, string? signature, params string?[] values) =>
        signature is not null && Hmac.MatchesHex(Hmac.Compute(HmacAlgorithm.Sha256, secretKey, string.Join(':', values.Select(value => value ?? string.Empty))), signature);

    /// <summary>A price as signatures carry it: no trailing zeros (<c>10.50</c> → <c>10.5</c>, <c>10.0</c> → <c>10</c>).</summary>
    public static string Price(decimal price) => price.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>The expected <c>X-IYZ-SIGNATURE-V3</c> of a notification (IYZ-12): the secret key and the fields, concatenated.</summary>
    public static byte[] Notification(string secretKey, params string?[] fields) =>
        Hmac.Compute(HmacAlgorithm.Sha256, secretKey, secretKey + string.Concat(fields.Select(field => field ?? string.Empty)));
}
