using System.Security.Cryptography;
using System.Text;
using Tahsil.Application.Common;

namespace Tahsil.Infrastructure.Security;

internal sealed class SecretEncryption(ISecretProtector protector) : ISecretEncryption
{
    public byte[] Encrypt(ReadOnlySpan<byte> secret, string purpose, Guid? owner = null) =>
        protector.Protect(secret, purpose, AssociatedData(owner));

    public byte[] Encrypt(string secret, string purpose, Guid? owner = null)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Encrypt(plaintext, purpose, owner);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> encryptedSecret, string purpose, Guid? owner = null) =>
        protector.Unprotect(encryptedSecret, purpose, AssociatedData(owner));

    public string DecryptString(ReadOnlySpan<byte> encryptedSecret, string purpose, Guid? owner = null)
    {
        var plaintext = Decrypt(encryptedSecret, purpose, owner);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(Guid? owner) => owner?.ToByteArray(bigEndian: true) ?? [];
}
