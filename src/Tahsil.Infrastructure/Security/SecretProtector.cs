using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Tahsil.Infrastructure.Security;

/// <summary>
/// AES-256-GCM with per-purpose keys derived from a master key through HKDF-SHA256.
/// Output format: <c>version 0x02 (1 byte) | master key ID (1 byte) | nonce (12 bytes) | ciphertext | tag (16 bytes)</c>.
/// The key ID lets the master key be rotated: earlier keys stay configured for decryption until <c>reencrypt</c>
/// has rewritten everything with the current one.
/// </summary>
internal sealed class SecretProtector : ISecretProtector
{
    private const byte FormatVersion = 2;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int NonceOffset = 2;
    private const int HeaderSize = NonceOffset + NonceSize;

    private readonly Dictionary<byte, byte[]> _masterKeys;
    private readonly ConcurrentDictionary<(byte KeyId, string Purpose), byte[]> _purposeKeys = new();

    public SecretProtector(IOptions<SecurityOptions> options)
    {
        CurrentKeyId = (byte)options.Value.MasterKeyId;
        _masterKeys = options.Value.DecodeKeys();
    }

    public byte CurrentKeyId { get; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, ReadOnlySpan<byte> associatedData = default)
    {
        var output = new byte[HeaderSize + plaintext.Length + TagSize];
        output[0] = FormatVersion;
        output[1] = CurrentKeyId;

        var nonce = output.AsSpan(NonceOffset, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(GetPurposeKey(CurrentKeyId, purpose), TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(HeaderSize, plaintext.Length), output.AsSpan(HeaderSize + plaintext.Length), associatedData);

        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose, ReadOnlySpan<byte> associatedData = default)
    {
        if (protectedData.Length < HeaderSize + TagSize || protectedData[0] != FormatVersion)
        {
            throw new CryptographicException("The protected data is malformed or uses an unsupported format version.");
        }

        var keyId = protectedData[1];
        if (!_masterKeys.ContainsKey(keyId))
        {
            throw new CryptographicException(
                $"The data was encrypted with master key {keyId}, which is not configured. Add it to {SecurityOptions.SectionName}:PreviousMasterKeys:{keyId}.");
        }

        var ciphertextLength = protectedData.Length - HeaderSize - TagSize;
        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(GetPurposeKey(keyId, purpose), TagSize);
        aes.Decrypt(
            protectedData.Slice(NonceOffset, NonceSize),
            protectedData.Slice(HeaderSize, ciphertextLength),
            protectedData.Slice(HeaderSize + ciphertextLength),
            plaintext,
            associatedData);

        return plaintext;
    }

    public bool IsCurrent(ReadOnlySpan<byte> protectedData) =>
        protectedData.Length >= HeaderSize && protectedData[0] == FormatVersion && protectedData[1] == CurrentKeyId;

    private byte[] GetPurposeKey(byte keyId, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        return _purposeKeys.GetOrAdd((keyId, purpose), static (key, masterKeys) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKeys[key.KeyId], outputLength: 32, salt: [], info: Encoding.UTF8.GetBytes(key.Purpose)),
            _masterKeys);
    }
}
