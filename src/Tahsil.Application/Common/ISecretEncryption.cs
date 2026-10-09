namespace Tahsil.Application.Common;

/// <summary>
/// Encrypts secrets that Tahsil must read back, such as provider credentials and card tokens, with the master key.
/// Each secret is bound to its purpose (<see cref="SecretPurposes"/>) and to the record it belongs to, so it cannot
/// be moved to another record or used for another purpose.
/// </summary>
public interface ISecretEncryption
{
    byte[] Encrypt(ReadOnlySpan<byte> secret, string purpose, Guid? owner = null);

    byte[] Encrypt(string secret, string purpose, Guid? owner = null);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The secret cannot be decrypted for this purpose and owner.</exception>
    byte[] Decrypt(ReadOnlySpan<byte> encryptedSecret, string purpose, Guid? owner = null);

    /// <inheritdoc cref="Decrypt"/>
    string DecryptString(ReadOnlySpan<byte> encryptedSecret, string purpose, Guid? owner = null);
}
