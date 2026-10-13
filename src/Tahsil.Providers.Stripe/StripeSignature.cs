using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tahsil.Providers.Stripe;

/// <summary>
/// Verifies <c>Stripe-Signature</c> (STR-11): a lowercase hex HMAC-SHA256 of <c>"{t}.{raw body}"</c> keyed with the UTF-8
/// bytes of the whole <c>whsec_…</c> secret, matched in constant time against each <c>v1</c>, within five minutes.
/// </summary>
internal static class StripeSignature
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static bool IsValid(string? header, byte[] body, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t" && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                timestamp = seconds;
            }
            else if (pair[0] == "v1")
            {
                signatures.Add(pair[1]);
            }
        }

        if (timestamp is not { } t || signatures.Count == 0 || (now - DateTimeOffset.FromUnixTimeSeconds(t)).Duration() > Tolerance)
        {
            return false;
        }

        var signed = Encoding.UTF8.GetBytes($"{t.ToString(CultureInfo.InvariantCulture)}.").Concat(body).ToArray();
        var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed)));
        return signatures.Any(signature => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), expected));
    }
}
