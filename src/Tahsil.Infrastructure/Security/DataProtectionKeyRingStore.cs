using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Security;

/// <summary>The Data Protection key ring, whose keys <see cref="MasterKeyXmlEncryptor"/> encrypted inside their XML.</summary>
internal sealed class DataProtectionKeyRingStore : IEncryptedStore
{
    public string Name => "data_protection_keys.xml";

    public async Task<int> ReencryptBatchAsync(TahsilDbContext context, ISecretProtector protector, int batchSize, CancellationToken cancellationToken)
    {
        var keys = await context.DataProtectionKeys
            .FromSql($"SELECT * FROM tahsil.data_protection_keys WHERE xml IS NOT NULL ORDER BY id FOR UPDATE")
            .ToListAsync(cancellationToken);

        var rewritten = 0;
        foreach (var key in keys)
        {
            var document = XElement.Parse(key.Xml!);
            var values = document.Descendants(MasterKeyXmlEncryptor.EncryptedKeyElement)
                .Select(element => element.Element(MasterKeyXmlEncryptor.ValueElement))
                .OfType<XElement>()
                .Where(value => !protector.IsCurrent(Convert.FromBase64String(value.Value)))
                .ToList();

            if (values.Count == 0)
            {
                continue;
            }

            foreach (var value in values)
            {
                var plaintext = protector.Unprotect(Convert.FromBase64String(value.Value), SecretPurposes.DataProtectionKeyRing);
                value.Value = Convert.ToBase64String(protector.Protect(plaintext, SecretPurposes.DataProtectionKeyRing));
            }

            key.Xml = document.ToString(SaveOptions.DisableFormatting);
            if (++rewritten == batchSize)
            {
                break;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return rewritten;
    }
}
