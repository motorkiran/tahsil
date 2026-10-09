using System.Security.Cryptography;
using System.Text;

namespace Tahsil.Application.Common;

/// <summary>
/// Hashes for secrets that are only checked, never read back. High-entropy secrets (API keys, hosted session
/// tokens) are stored as SHA-256; tokens derived from data (hosted invoice links) are HMAC-SHA256 with a stored key.
/// Short secrets are never stored with an unkeyed hash.
/// </summary>
public static class SecretHashing
{
    public static byte[] Sha256(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public static byte[] Hmac(ReadOnlySpan<byte> key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>Compares in constant time, so the comparison does not reveal how much of a guess was right.</summary>
    public static bool AreEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) => CryptographicOperations.FixedTimeEquals(left, right);
}
