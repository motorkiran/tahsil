using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Domain.Invoices;

namespace Tahsil.Application.Invoices;

/// <summary>
/// Hosted invoice links (INV-24): <c>/invoices/{token}</c> with a token that needs no storage, the invoice's ID and an
/// HMAC-SHA256 of it with the link key of the current <c>LinkKeyVersion</c>, so any link can be made again for an
/// email and none can be guessed. Changing the version invalidates every link.
/// </summary>
public sealed class InvoiceLinks(IOptions<InvoiceOptions> options, IPublicUrls urls, ISecretEncryption encryption, IServiceScopeFactory scopes, TimeProvider time)
{
    private const int IdLength = 16;
    private readonly ConcurrentDictionary<int, byte[]> _keys = new();

    /// <summary>Creates the current version's key if it is missing, when the database is prepared.</summary>
    public async Task EnsureKeyAsync(ITahsilDbContext context, CancellationToken cancellationToken)
    {
        var version = options.Value.LinkKeyVersion;
        if (!await context.InvoiceLinkKeys.AnyAsync(key => key.Version == version, cancellationToken))
        {
            context.InvoiceLinkKeys.Add(InvoiceLinkKey.Create(version, encryption.Encrypt(RandomNumberGenerator.GetBytes(32), SecretPurposes.InvoiceLinkKey), time.GetUtcNow()));
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The hosted page of a finalized invoice; drafts have none.</summary>
    public Uri? UrlFor(Invoice invoice) => invoice.Status == InvoiceStatus.Draft ? null : urls.Build($"invoices/{TokenFor(invoice.Id)}");

    public string TokenFor(Guid invoiceId)
    {
        Span<byte> token = stackalloc byte[IdLength + 32];
        invoiceId.TryWriteBytes(token[..IdLength]);
        Mac(invoiceId, token[IdLength..]);
        return Base64Url.EncodeToString(token);
    }

    /// <summary>The invoice a token names, or <see langword="null"/> when it is not one of the current key's.</summary>
    public Guid? Verify(string token)
    {
        Span<byte> bytes = stackalloc byte[IdLength + 32];
        if (!Base64Url.TryDecodeFromChars(token, bytes, out var length) || length != bytes.Length)
        {
            return null;
        }

        var id = new Guid(bytes[..IdLength]);
        Span<byte> expected = stackalloc byte[32];
        Mac(id, expected);
        return CryptographicOperations.FixedTimeEquals(expected, bytes[IdLength..]) ? id : null;
    }

    private void Mac(Guid invoiceId, Span<byte> destination) =>
        HMACSHA256.HashData(_keys.GetOrAdd(options.Value.LinkKeyVersion, Load), Encoding.UTF8.GetBytes($"invoice:{invoiceId}"), destination);

    /// <summary>Reads a version's key once per process; the database preparation created it.</summary>
    private byte[] Load(int version)
    {
        using var scope = scopes.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<ITahsilDbContext>().InvoiceLinkKeys.AsNoTracking().SingleOrDefault(key => key.Version == version)
            ?? throw new InvalidOperationException($"The invoice link key of version {version} does not exist; prepare the database (the migrate command or startup) first.");
        return encryption.Decrypt(stored.Key, SecretPurposes.InvoiceLinkKey);
    }
}
