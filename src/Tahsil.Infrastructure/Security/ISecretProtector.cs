namespace Tahsil.Infrastructure.Security;

/// <summary>
/// Encrypts secrets that must be read back later (provider credentials, card tokens, the Data Protection key ring)
/// with keys derived from the master key. Each purpose gets its own key, so output produced for one purpose is
/// useless for another, and associated data binds a ciphertext to the record it belongs to.
/// </summary>
internal interface ISecretProtector
{
    /// <summary>The ID of the master key that <see cref="Protect"/> uses.</summary>
    byte CurrentKeyId { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, ReadOnlySpan<byte> associatedData = default);

    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The data was tampered with, protected for another purpose or record, or with a master key that is not configured.
    /// </exception>
    byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose, ReadOnlySpan<byte> associatedData = default);

    /// <summary>Whether <paramref name="protectedData"/> was protected with the current master key.</summary>
    bool IsCurrent(ReadOnlySpan<byte> protectedData);
}
