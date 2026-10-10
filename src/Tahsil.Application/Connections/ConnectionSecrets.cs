using System.Text.Json;
using Tahsil.Application.Common;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Connections;

/// <summary>Encrypts a connection's credentials as one JSON object bound to the connection (SEC-9).</summary>
public sealed class ConnectionSecrets(ISecretEncryption encryption)
{
    public Func<Guid, byte[]> Encrypt(IReadOnlyDictionary<string, string> credentials) =>
        id => encryption.Encrypt(JsonSerializer.SerializeToUtf8Bytes(credentials), SecretPurposes.ConnectionCredentials, id);

    public IReadOnlyDictionary<string, string> Decrypt(ProviderConnection connection) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(encryption.Decrypt(connection.Credentials, SecretPurposes.ConnectionCredentials, connection.Id))
            ?? [];
}
