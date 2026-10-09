using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

namespace Tahsil.Infrastructure.Security;

internal sealed class ConfigureKeyRingEncryption(ISecretProtector protector) : IConfigureOptions<KeyManagementOptions>
{
    public void Configure(KeyManagementOptions options) => options.XmlEncryptor = new MasterKeyXmlEncryptor(protector);
}
