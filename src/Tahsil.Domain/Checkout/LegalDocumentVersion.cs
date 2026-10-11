namespace Tahsil.Domain.Checkout;

/// <summary>
/// The content of a legal document's version in a locale, as first seen: content cannot change under the same version,
/// so acceptances recorded against it stay provable (CHK-29).
/// </summary>
public sealed class LegalDocumentVersion
{
    private LegalDocumentVersion()
    {
    }

    public string Key { get; private init; } = string.Empty;

    public string Version { get; private init; } = string.Empty;

    public string Locale { get; private init; } = string.Empty;

    /// <summary>SHA-256 of the template's source.</summary>
    public byte[] ContentHash { get; private init; } = [];

    public DateTimeOffset RegisteredAt { get; private init; }

    public static LegalDocumentVersion Register(string key, string version, string locale, byte[] contentHash, DateTimeOffset now) =>
        new() { Key = key, Version = version, Locale = locale, ContentHash = contentHash, RegisteredAt = now };
}
