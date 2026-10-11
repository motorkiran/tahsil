namespace Tahsil.Domain.Invoices;

/// <summary>
/// The HMAC key of hosted invoice links (INV-24) for one version of <c>Tahsil:Invoices:LinkKeyVersion</c>: random,
/// encrypted with the master key. A new version makes every earlier link invalid.
/// </summary>
public sealed class InvoiceLinkKey
{
    private InvoiceLinkKey()
    {
    }

    public Guid Id { get; private init; }

    public int Version { get; private init; }

    /// <summary>The key, encrypted (<c>invoices.link_key</c>).</summary>
    public byte[] Key { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private init; }

    public static InvoiceLinkKey Create(int version, byte[] encryptedKey, DateTimeOffset now) => new() { Id = Guid.CreateVersion7(now), Version = version, Key = encryptedKey, CreatedAt = now };
}
