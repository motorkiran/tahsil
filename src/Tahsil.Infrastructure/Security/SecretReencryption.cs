using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Security;

/// <summary>
/// Rewrites every value encrypted with an earlier master key under the current one, in batches of short
/// transactions, so the earlier key can then be removed from the configuration. Safe to run again or while
/// Tahsil is serving: it only touches values the current key did not encrypt.
/// </summary>
internal sealed partial class SecretReencryption(
    TahsilDbContext context,
    ISecretProtector protector,
    IEnumerable<IEncryptedStore> stores,
    ILogger<SecretReencryption> logger)
{
    public const int BatchSize = 500;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var store in stores)
        {
            var rewritten = 0;
            int batch;
            do
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                batch = await store.ReencryptBatchAsync(context, protector, BatchSize, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                context.ChangeTracker.Clear();
                rewritten += batch;
            }
            while (batch > 0);

            LogStoreReencrypted(logger, store.Name, rewritten, protector.CurrentKeyId);
            total += rewritten;
        }

        return total;
    }

    [LoggerMessage(LogLevel.Information, "Re-encrypted {Count} values of {Store} with master key {KeyId}")]
    private static partial void LogStoreReencrypted(ILogger logger, string store, int count, byte keyId);
}

public static class SecretReencryptionExtensions
{
    /// <inheritdoc cref="SecretReencryption"/>
    public static async Task<int> ReencryptSecretsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SecretReencryption>().RunAsync(cancellationToken);
    }
}
