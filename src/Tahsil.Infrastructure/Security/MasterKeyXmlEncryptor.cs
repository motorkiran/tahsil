using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Tahsil.Application.Common;

namespace Tahsil.Infrastructure.Security;

/// <summary>
/// Encrypts the ASP.NET Core Data Protection key ring with the master key before it is stored in the database.
/// </summary>
internal sealed class MasterKeyXmlEncryptor(ISecretProtector protector) : IXmlEncryptor
{
    public const string EncryptedKeyElement = "encryptedKey";
    public const string ValueElement = "value";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));

        try
        {
            var protectedBytes = protector.Protect(plaintext, SecretPurposes.DataProtectionKeyRing);

            var encryptedElement = new XElement(
                EncryptedKeyElement,
                new XComment(" This key is encrypted with the Tahsil master key. "),
                new XElement(ValueElement, Convert.ToBase64String(protectedBytes)));

            return new EncryptedXmlInfo(encryptedElement, typeof(MasterKeyXmlDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
