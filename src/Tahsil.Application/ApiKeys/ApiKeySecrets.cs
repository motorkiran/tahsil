using System.Buffers.Text;
using System.Security.Cryptography;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.ApiKeys;

namespace Tahsil.Application.ApiKeys;

/// <summary>
/// API key secrets, as <see cref="ApiKeyFormat"/> describes them. Being random, they need no slow hashing; SHA-256 is
/// enough to keep them unreadable at rest and quick to look up.
/// </summary>
internal static class ApiKeySecrets
{
    public static (string Secret, string DisplayPrefix, byte[] Hash) Generate(TahsilMode mode)
    {
        var prefix = mode == TahsilMode.Live ? ApiKeyFormat.LivePrefix : ApiKeyFormat.TestPrefix;
        var secret = prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (secret, secret[..ApiKey.DisplayPrefixLength], SecretHashing.Sha256(secret));
    }
}
