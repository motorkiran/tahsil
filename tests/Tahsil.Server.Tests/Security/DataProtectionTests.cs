using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Security;

namespace Tahsil.Server.Tests.Security;

public sealed class DataProtectionTests(TahsilServerFixture server)
{
    private const string TestPurpose = "tests.secret";

    private static readonly string Key1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string Key2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "FND-33")]
    public async Task Keys_SurviveARestart_AndAreUnreadableWithoutTheMasterKey()
    {
        var database = server.NewDatabase("dataprotection");
        string payload;
        await using (var first = Host(database, Key1))
        {
            payload = Protector(first).Protect("anti-forgery");
        }

        await using var restarted = Host(database, Key1);
        await using var stranger = Host(database, Key2);

        Protector(restarted).Unprotect(payload).ShouldBe("anti-forgery");
        Should.Throw<CryptographicException>(() => Protector(stranger).Unprotect(payload));

        // The host with the wrong master key cannot read the existing key, so it made one of its own.
        var keys = await QueryAsync(restarted, context => context.DataProtectionKeys.Select(key => key.Xml!).ToListAsync(CancellationToken));
        keys.ShouldNotBeEmpty();
        keys.ShouldAllBe(xml => xml.Contains("encryptedKey") && !xml.Contains("<masterKey"));
    }

    [Fact]
    [Trait("Requirement", "FND-30")]
    public async Task Reencrypt_MovesEverySecretToTheCurrentMasterKey()
    {
        var database = server.NewDatabase("rotation");
        var owner = Guid.CreateVersion7();
        string payload;
        await using (var before = Host(database, Key1))
        {
            payload = Protector(before).Protect("session");
            await using var scope = before.Services.CreateAsyncScope();
            var encrypted = scope.ServiceProvider.GetRequiredService<ISecretEncryption>().Encrypt("credential", TestPurpose, owner);
            var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
            await context.Database.ExecuteSqlAsync($"CREATE TABLE tahsil.test_secrets (id uuid PRIMARY KEY, secret bytea)", CancellationToken);
            await context.Database.ExecuteSqlAsync($"INSERT INTO tahsil.test_secrets VALUES ({owner}, {encrypted})", CancellationToken);
        }

        int reencrypted;
        await using (var rotated = Host(database, Key2, keyId: 2, previousKey1: Key1))
        {
            Protector(rotated).Unprotect(payload).ShouldBe("session");
            reencrypted = await rotated.Services.ReencryptSecretsAsync(CancellationToken);
            (await rotated.Services.ReencryptSecretsAsync(CancellationToken)).ShouldBe(0);
        }

        await using var after = Host(database, Key2, keyId: 2);
        // The key ring, the simulator connection's credentials and the test secret.
        reencrypted.ShouldBe(3);
        Protector(after).Unprotect(payload).ShouldBe("session");
        var stored = await ReadSecretAsync(database, owner);
        stored[1].ShouldBe((byte)2);
        after.Services.GetRequiredService<ISecretEncryption>().DecryptString(stored, TestPurpose, owner).ShouldBe("credential");
    }

    [Fact]
    [Trait("Requirement", "FND-30")]
    public void EverySecretColumn_IsRegisteredForReencryption()
    {
        server.Services.GetServices<IEncryptedStore>().Select(store => store.Name)
            .ShouldBe(["data_protection_keys.xml", "customers.tax_id", "provider_connections.credentials", "invoices.customer_tax_id", "payment_methods.token", "provider_notifications.raw_body", "webhook_endpoints.secret", "webhook_endpoints.previous_secret"], ignoreOrder: true);
    }

    private WebApplicationFactory<Program> Host(string database, string masterKey, int keyId = 1, string? previousKey1 = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = database,
            ["Tahsil:Security:MasterKey"] = masterKey,
            ["Tahsil:Security:MasterKeyId"] = keyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (previousKey1 is not null)
        {
            settings["Tahsil:Security:PreviousMasterKeys:1"] = previousKey1;
        }

        return server.WithSettings(settings, services => services.AddSingleton<IEncryptedStore>(new EncryptedColumn("test_secrets", "secret", TestPurpose)));
    }

    private static IDataProtector Protector(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Tahsil.Tests");

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> query)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private static async Task<byte[]> ReadSecretAsync(string database, Guid id)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT secret FROM tahsil.test_secrets WHERE id = $1", connection) { Parameters = { new() { Value = id } } };
        return (byte[])(await command.ExecuteScalarAsync(CancellationToken))!;
    }
}
