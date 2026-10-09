using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;

namespace Tahsil.Infrastructure.Security;

/// <summary>
/// Decrypts key ring entries written by <see cref="MasterKeyXmlEncryptor"/>. Data Protection stores this type's
/// name next to every key and creates it through its <see cref="IServiceProvider"/> constructor, so the type
/// must not be renamed or moved.
/// </summary>
internal sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var value = (string?)encryptedElement.Element(MasterKeyXmlEncryptor.ValueElement)
            ?? throw new CryptographicException("The encrypted key element has no value.");

        var plaintext = services.GetRequiredService<ISecretProtector>()
            .Unprotect(Convert.FromBase64String(value), SecretPurposes.DataProtectionKeyRing);

        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
