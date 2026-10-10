using System.Security.Cryptography;
using System.Text;

namespace Tahsil.Providers.Abstractions;

public enum HmacAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>Signatures for provider requests and notifications.</summary>
public static class Hmac
{
    public static byte[] Compute(HmacAlgorithm algorithm, string key, string message) =>
        Compute(algorithm, Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));

    public static byte[] Compute(HmacAlgorithm algorithm, byte[] key, byte[] message) => algorithm switch
    {
#pragma warning disable CA5350 // Some providers still sign with HMAC-SHA1; the algorithm is theirs, not a choice.
        HmacAlgorithm.Sha1 => HMACSHA1.HashData(key, message),
#pragma warning restore CA5350
        HmacAlgorithm.Sha256 => HMACSHA256.HashData(key, message),
        HmacAlgorithm.Sha512 => HMACSHA512.HashData(key, message),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static string Base64(HmacAlgorithm algorithm, string key, string message) => Convert.ToBase64String(Compute(algorithm, key, message));

    public static string Hex(HmacAlgorithm algorithm, string key, string message) => Convert.ToHexStringLower(Compute(algorithm, key, message));

    /// <summary>Compares a received signature with the expected one in constant time.</summary>
    public static bool Matches(string expected, string? received) =>
        received is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received));

    /// <summary>Compares a received hexadecimal signature with the expected bytes in constant time, ignoring case.</summary>
    public static bool MatchesHex(byte[] expected, string? received)
    {
        if (received is null || received.Length != expected.Length * 2)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(received);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, bytes);
    }
}
